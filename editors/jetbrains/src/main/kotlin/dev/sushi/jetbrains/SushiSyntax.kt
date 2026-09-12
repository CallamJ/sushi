package dev.sushi.jetbrains

import com.intellij.lexer.LexerBase
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.project.Project
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.openapi.vfs.VirtualFile

/**
 * Local lexical highlighting keeps .sushi files readable before the language
 * server starts (and if it is unavailable). Semantic tokens from the LSP add
 * the more precise declaration/reference colouring on top.
 */
class SushiSyntaxHighlighterFactory : SyntaxHighlighterFactory() {
    override fun getSyntaxHighlighter(project: Project?, virtualFile: VirtualFile?): SyntaxHighlighter = SushiSyntaxHighlighter()
}

internal object SushiTokenTypes {
    val IDENTIFIER = IElementType("SUSHI_IDENTIFIER", SushiLanguage)
    val FUNCTION = IElementType("SUSHI_FUNCTION", SushiLanguage)
    val KEYWORD = IElementType("SUSHI_KEYWORD", SushiLanguage)
    val STRING = IElementType("SUSHI_STRING", SushiLanguage)
    val NUMBER = IElementType("SUSHI_NUMBER", SushiLanguage)
    val COMMENT = IElementType("SUSHI_COMMENT", SushiLanguage)
    val OPERATOR = IElementType("SUSHI_OPERATOR", SushiLanguage)
}

private class SushiSyntaxHighlighter : SyntaxHighlighterBase() {
    override fun getHighlightingLexer() = SushiLexer()

    override fun getTokenHighlights(tokenType: IElementType): Array<TextAttributesKey> = when (tokenType) {
        SushiTokenTypes.KEYWORD -> pack(DefaultLanguageHighlighterColors.KEYWORD)
        SushiTokenTypes.FUNCTION -> pack(DefaultLanguageHighlighterColors.FUNCTION_DECLARATION)
        SushiTokenTypes.STRING -> pack(DefaultLanguageHighlighterColors.STRING)
        SushiTokenTypes.NUMBER -> pack(DefaultLanguageHighlighterColors.NUMBER)
        SushiTokenTypes.COMMENT -> pack(DefaultLanguageHighlighterColors.LINE_COMMENT)
        SushiTokenTypes.OPERATOR -> pack(DefaultLanguageHighlighterColors.OPERATION_SIGN)
        else -> TextAttributesKey.EMPTY_ARRAY
    }
}

internal class SushiLexer : LexerBase() {
    private var text: CharSequence = ""
    private var limit = 0
    private var position = 0
    private var tokenStart = 0
    private var tokenEnd = 0
    private var tokenType: IElementType? = null

    override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
        text = buffer
        limit = endOffset
        position = startOffset
        tokenStart = startOffset
        tokenEnd = startOffset
        tokenType = null
        advance()
    }

    override fun getState() = 0
    override fun getTokenType(): IElementType? = tokenType
    override fun getTokenStart() = tokenStart
    override fun getTokenEnd() = tokenEnd
    override fun getBufferSequence(): CharSequence = text
    override fun getBufferEnd() = limit

    override fun advance() {
        if (position >= limit) {
            tokenStart = limit
            tokenEnd = limit
            tokenType = null
            return
        }

        tokenStart = position
        val current = text[position]
        tokenType = when {
            current.isWhitespace() -> {
                while (position < limit && text[position].isWhitespace()) position++
                TokenType.WHITE_SPACE
            }
            current == '/' && position + 1 < limit && text[position + 1] == '/' -> {
                position += 2
                while (position < limit && text[position] != '\n') position++
                SushiTokenTypes.COMMENT
            }
            current == '/' && position + 1 < limit && text[position + 1] == '*' -> {
                position += 2
                while (position + 1 < limit && !(text[position] == '*' && text[position + 1] == '/')) position++
                if (position + 1 < limit) position += 2 else position = limit
                SushiTokenTypes.COMMENT
            }
            current == '\'' || current == '"' -> {
                val quote = current
                position++
                while (position < limit) {
                    if (text[position] == '\\' && position + 1 < limit) position += 2
                    else if (text[position++] == quote) break
                }
                SushiTokenTypes.STRING
            }
            current.isDigit() -> {
                while (position < limit && (text[position].isDigit() || text[position] == '.')) position++
                SushiTokenTypes.NUMBER
            }
            current.isLetter() || current == '_' -> {
                while (position < limit && (text[position].isLetterOrDigit() || text[position] == '_')) position++
                val word = text.subSequence(tokenStart, position).toString()
                when {
                    word in KEYWORDS -> SushiTokenTypes.KEYWORD
                    // Built-in types are language keywords. User-defined class
                    // names deliberately remain ordinary identifiers.
                    word in PRIMITIVE_TYPES -> SushiTokenTypes.KEYWORD
                    isFunctionDeclaration(position) -> SushiTokenTypes.FUNCTION
                    else -> SushiTokenTypes.IDENTIFIER
                }
            }
            else -> {
                position++
                SushiTokenTypes.OPERATOR
            }
        }
        tokenEnd = position
    }

    // Sushi declares functions as name(parameters) { ... } or name(parameters) -> expression.
    // Looking ahead only to the immediate body delimiter distinguishes declarations from calls.
    private fun isFunctionDeclaration(afterName: Int): Boolean {
        var cursor = afterName
        while (cursor < limit && text[cursor].isWhitespace()) cursor++
        if (cursor >= limit || text[cursor] != '(') return false
        var depth = 0
        var quote: Char? = null
        while (cursor < limit) {
            val character = text[cursor]
            if (quote != null) {
                if (character == '\\' && cursor + 1 < limit) cursor += 2
                else { if (character == quote) quote = null; cursor++ }
                continue
            }
            when (character) {
                '\'', '"' -> quote = character
                '(' -> depth++
                ')' -> if (--depth == 0) {
                    cursor++
                    while (cursor < limit && text[cursor].isWhitespace()) cursor++
                    return cursor < limit && (text[cursor] == '{' ||
                        (text[cursor] == '-' && cursor + 1 < limit && text[cursor + 1] == '>'))
                }
            }
            cursor++
        }
        return false
    }

    private companion object {
        val KEYWORDS = setOf(
            "as", "box", "break", "case", "catch", "class", "continue", "default", "do", "else", "enum",
            "export", "false", "finally", "for", "func", "function", "if", "import", "in", "let", "match",
            "module", "namespace", "new", "null", "private", "public", "return", "static", "switch", "throw",
            "true", "try", "use", "var", "while"
        )
        val PRIMITIVE_TYPES = setOf("any", "array", "bool", "float", "int", "object", "string", "void")
    }
}
