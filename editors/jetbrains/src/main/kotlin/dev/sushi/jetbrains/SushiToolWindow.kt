package dev.sushi.jetbrains

import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.content.ContentFactory
import com.intellij.ui.components.JBTextArea
import java.awt.BorderLayout
import java.awt.Font
import javax.swing.JPanel
import javax.swing.JScrollPane

class SushiToolWindowFactory : ToolWindowFactory {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        SushiToolWindow.ensureWelcomeContent(toolWindow)
    }
}

object SushiToolWindow {
    fun show(project: Project, title: String, text: String, readOnly: Boolean = true) {
        val toolWindow = com.intellij.openapi.wm.ToolWindowManager.getInstance(project)
            .getToolWindow("Sushi") ?: return
        toolWindow.show {
            val area = JBTextArea(text).apply {
                isEditable = !readOnly
                lineWrap = false
                wrapStyleWord = false
                font = Font("Monospaced", Font.PLAIN, 12)
            }
            val panel = JPanel(BorderLayout()).apply { add(JScrollPane(area), BorderLayout.CENTER) }
            val content = ContentFactory.getInstance().createContent(panel, title, false)
            toolWindow.contentManager.addContent(content)
            toolWindow.contentManager.setSelectedContent(content)
        }
    }

    fun ensureWelcomeContent(toolWindow: ToolWindow) {
        if (toolWindow.contentManager.contentCount == 0)
            toolWindow.contentManager.addContent(ContentFactory.getInstance().createContent(JPanel(), "Sushi", false))
    }
}
