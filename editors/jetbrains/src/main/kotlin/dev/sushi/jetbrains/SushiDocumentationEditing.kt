package dev.sushi.jetbrains

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity

/**
 * Handles documentation editing from document changes rather than an Enter
 * action delegate. This remains reliable when an IDE or keymap intercepts the
 * Enter action before language-specific delegates are consulted.
 */
class SushiDocumentationEditingStartupActivity : ProjectActivity {
    override suspend fun execute(project: Project) {
        EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                if (!event.newFragment.contains('\n')) return
                val document = event.document
                val file = FileDocumentManager.getInstance().getFile(document) ?: return
                if (file.fileType != SushiFileType) return
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    WriteCommandAction.runWriteCommandAction(project, Runnable {
                        SushiDocumentationEditing.expandAfterEnter(document, project)
                    })
                }
            }
        }, project)
    }
}

object SushiDocumentationEditing {
    private val documentationLine = Regex("(\\s*)///(.*)")
    fun expandAfterEnter(document: Document, project: Project) {
        val editor = EditorFactory.getInstance().getEditors(document, project).firstOrNull() ?: return
        val line = document.getLineNumber(editor.caretModel.offset)
        if (line == 0) return
        val previous = line - 1
        val previousText = lineText(document, previous)
        val documentation = documentationLine.matchEntire(previousText) ?: return
        val indentation = documentation.groupValues[1]

        val currentText = lineText(document, line)
        if (currentText.trimStart().startsWith("///")) return
        val prefix = indentation + "/// "
        document.insertString(document.getLineStartOffset(line), prefix)
        editor.caretModel.moveToOffset(document.getLineStartOffset(line) + prefix.length)
    }

    private fun lineText(document: Document, line: Int) =
        document.charsSequence.subSequence(document.getLineStartOffset(line), document.getLineEndOffset(line)).toString()
}
