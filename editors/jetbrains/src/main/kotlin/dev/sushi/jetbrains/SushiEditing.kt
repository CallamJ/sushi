package dev.sushi.jetbrains

import com.intellij.lang.BracePair
import com.intellij.lang.PairedBraceMatcher
import com.intellij.openapi.actionSystem.DataContext
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.actionSystem.EditorActionHandler
import com.intellij.openapi.fileTypes.FileType
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiFile
import com.intellij.psi.tree.IElementType
import com.intellij.codeInsight.editorActions.enter.EnterHandlerDelegate
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.openapi.util.Ref

class SushiBraceMatcher : PairedBraceMatcher {
    override fun getPairs() = arrayOf(
        BracePair(SushiTokenTypes.LEFT_PAREN, SushiTokenTypes.RIGHT_PAREN, false),
        BracePair(SushiTokenTypes.LEFT_BRACKET, SushiTokenTypes.RIGHT_BRACKET, false),
        BracePair(SushiTokenTypes.LEFT_BRACE, SushiTokenTypes.RIGHT_BRACE, true)
    )

    override fun isPairedBracesAllowedBeforeType(leftBraceType: IElementType, contextType: IElementType?) = true
    override fun getCodeConstructStart(file: PsiFile, openingBraceOffset: Int) = openingBraceOffset
}

class SushiQuoteTypedHandler : TypedHandlerDelegate() {
    override fun beforeCharTyped(char: Char, project: Project, editor: Editor, file: PsiFile, fileType: FileType): Result {
        if (file.language != SushiLanguage || char !in QUOTES) return Result.CONTINUE
        val offset = editor.caretModel.offset
        val text = editor.document.charsSequence
        if (offset < text.length && text[offset] == char) {
            editor.caretModel.moveToOffset(offset + 1)
            return Result.STOP
        }
        return Result.CONTINUE
    }

    override fun charTyped(char: Char, project: Project, editor: Editor, file: PsiFile): Result {
        if (file.language != SushiLanguage || char !in QUOTES) return Result.CONTINUE
        val offset = editor.caretModel.offset
        val text = editor.document.charsSequence
        if (!shouldAutoPair(text, offset - 1, char)) return Result.CONTINUE
        if (offset < text.length && text[offset] == char) return Result.CONTINUE
        editor.document.insertString(offset, char.toString())
        editor.caretModel.moveToOffset(offset)
        return Result.STOP
    }

    private fun shouldAutoPair(text: CharSequence, typedOffset: Int, quote: Char): Boolean {
        if (typedOffset < 0) return false
        var lineStart = typedOffset
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--
        val before = text.subSequence(lineStart, typedOffset).toString()
        if (before.contains("//")) return false
        var quotes = 0
        var index = 0
        while (index < before.length) {
            if (before[index] == '\\') index += 2
            else {
                if (before[index] == quote) quotes++
                index++
            }
        }
        return quotes % 2 == 0
    }

    private companion object { val QUOTES = setOf('\'', '"') }
}

class SushiEnterHandler : EnterHandlerDelegate {
    override fun preprocessEnter(
        file: PsiFile,
        editor: Editor,
        caretOffset: Ref<Int>,
        caretAdvance: Ref<Int>,
        dataContext: DataContext,
        originalHandler: EditorActionHandler?
    ) = EnterHandlerDelegate.Result.Continue

    override fun postProcessEnter(file: PsiFile, editor: Editor, dataContext: DataContext): EnterHandlerDelegate.Result {
        if (file.language != SushiLanguage) return EnterHandlerDelegate.Result.Continue
        val document = editor.document
        val offset = editor.caretModel.offset
        val line = document.getLineNumber(offset)
        if (line == 0) return EnterHandlerDelegate.Result.Continue
        // IntelliJ may insert indentation before placing the caret on a new line.
        // In that case `offset - 1` still belongs to the new line, so use the
        // line index rather than the caret offset to find the source comment.
        val previous = line - 1
        val previousText = document.charsSequence.subSequence(document.getLineStartOffset(previous), document.getLineEndOffset(previous)).toString()
        val bareDocumentation = BARE_DOC_COMMENT.matchEntire(previousText)
        if (bareDocumentation != null) {
            val declarationLine = nextDeclarationLine(document, line)
            val template = declarationLine?.let {
                documentationTemplate(document.charsSequence.subSequence(document.getLineStartOffset(it), document.getLineEndOffset(it)).toString(), bareDocumentation.groupValues[1])
            }
            if (template != null) {
                val start = document.getLineStartOffset(previous)
                val declarationStart = document.getLineStartOffset(declarationLine)
                document.replaceString(start, declarationStart, "$template\n")
                editor.caretModel.moveToOffset(start + bareDocumentation.groupValues[1].length + "/// ".length)
                return EnterHandlerDelegate.Result.Stop
            }
        }
        val match = DOC_COMMENT.matchEntire(previousText)
        if (match != null) {
            val currentText = document.charsSequence.subSequence(document.getLineStartOffset(line), document.getLineEndOffset(line)).toString()
            // SushiCommenter lets IntelliJ insert this prefix itself. Retain the
            // fallback for editor builds that do not invoke the built-in handler.
            if (currentText.trimStart().startsWith("///")) return EnterHandlerDelegate.Result.Continue
            val prefix = match.groupValues[1] + "/// "
            document.insertString(offset, prefix)
            editor.caretModel.moveToOffset(offset + prefix.length)
            return EnterHandlerDelegate.Result.Stop
        }

        val text = document.charsSequence
        if (offset < text.length && text[offset] == '}' && previousText.trimEnd().endsWith('{')) {
            val indentation = previousText.takeWhile { it == ' ' || it == '\t' } + "    "
            document.insertString(offset, indentation)
            editor.caretModel.moveToOffset(offset + indentation.length)
            return EnterHandlerDelegate.Result.Stop
        }
        return EnterHandlerDelegate.Result.Continue
    }

    private fun nextDeclarationLine(document: com.intellij.openapi.editor.Document, from: Int): Int? {
        for (line in from until document.lineCount) {
            val text = document.charsSequence.subSequence(document.getLineStartOffset(line), document.getLineEndOffset(line)).toString()
            if (text.isNotBlank() && BARE_DOC_COMMENT.matchEntire(text) == null) return line
        }
        return null
    }

    private fun documentationTemplate(rawDeclaration: String, indentation: String): String? {
        val declaration = rawDeclaration.trimStart()
        val classOrEnum = Regex("^(?:export\\s+)?(?:class|enum)\\s+([A-Za-z_]\\w*)").find(declaration)
        if (classOrEnum != null) return "$indentation/// TODO: Describe ${classOrEnum.groupValues[1]}."

        val constructor = Regex("^new\\s*\\(([^)]*)\\)").find(declaration)
        if (constructor != null) return documentationLines(indentation, "constructor", constructor.groupValues[1], returns = false)

        val callable = Regex("^(?:export\\s+)?(?:(?<return>[A-Za-z_]\\w*)\\s+)?(?<name>[A-Za-z_]\\w*)\\s*\\((?<parameters>[^)]*)\\)").find(declaration)
        if (callable != null) {
            val returnType = callable.groups["return"]?.value
            return documentationLines(indentation, callable.groups["name"]!!.value, callable.groups["parameters"]!!.value, returns = !returnType.equals("void", ignoreCase = true))
        }

        val field = Regex("^(?:export\\s+)?(?:[A-Za-z_]\\w*\\s+)?([A-Za-z_]\\w*)").find(declaration)
        return field?.let { "$indentation/// TODO: Describe ${it.groupValues[1]}." }
    }

    private fun documentationLines(indentation: String, name: String, parameters: String, returns: Boolean): String {
        val lines = mutableListOf("$indentation/// TODO: Describe $name.")
        parameters.split(',')
            .mapNotNull { parameter -> Regex("([A-Za-z_]\\w*)\\s*$").find(parameter.substringBefore('=').trim())?.groupValues?.get(1) }
            .forEach { parameter -> lines += "$indentation/// @param $parameter TODO." }
        if (returns) lines += "$indentation/// @returns TODO."
        return lines.joinToString("\n")
    }

    private companion object {
        val BARE_DOC_COMMENT = Regex("(\\s*)///\\s*")
        val DOC_COMMENT = Regex("(\\s*)///(?:.*)")
    }
}
