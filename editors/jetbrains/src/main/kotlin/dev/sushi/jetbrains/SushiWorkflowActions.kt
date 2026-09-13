package dev.sushi.jetbrains

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.icons.AllIcons
import com.intellij.openapi.fileEditor.ex.FileEditorManagerEx
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.Task
import com.intellij.openapi.progress.ProgressManager
import com.intellij.platform.lsp.api.LspClientManager
import java.nio.file.Files
import java.nio.file.Path

/**
 * The platform LSP API intentionally does not expose a generic custom-request client.
 * These actions preserve the important behaviour anyway: they materialize the current
 * unsaved editor text in a private temp directory, never beside the source document.
 */
abstract class SushiWorkflowAction(private val verb: String) : AnAction() {
    override fun update(event: AnActionEvent) {
        val editor = event.getData(com.intellij.openapi.actionSystem.CommonDataKeys.EDITOR)
        event.presentation.isEnabledAndVisible = event.project != null && editor?.virtualFile?.extension == "sushi"
        event.presentation.icon = when (verb) {
            "Run" -> AllIcons.Actions.Execute
            "Check" -> AllIcons.Actions.Refresh
            else -> AllIcons.Actions.ShowAsTree
        }
    }

    override fun actionPerformed(event: AnActionEvent) {
        val project = event.project ?: return
        val editor = event.getData(com.intellij.openapi.actionSystem.CommonDataKeys.EDITOR) ?: return
        val document = editor.document
        val source = document.text
        val settings = SushiSettings.getInstance().state
        ProgressManager.getInstance().run(object : Task.Backgroundable(project, "Sushi: $verb", false) {
            override fun run(indicator: ProgressIndicator) {
                val directory = Files.createTempDirectory("sushi-idea-")
                val input = directory.resolve("buffer.sushi")
                Files.writeString(input, source)
                try {
                    perform(project, settings, input, directory, indicator)
                } finally {
                    // Preview holds its generated file open, so that action opts out of cleanup.
                    if (verb != "Preview Generated Output") directory.toFile().deleteRecursively()
                }
            }
        })
    }

    protected abstract fun perform(project: com.intellij.openapi.project.Project, settings: SushiSettings.Data, input: Path, directory: Path, indicator: ProgressIndicator)

    protected fun execute(settings: SushiSettings.Data, vararg arguments: String): Pair<Int, String> {
        val process = ProcessBuilder(listOf(settings.serverPath) + arguments).redirectErrorStream(true).start()
        val output = process.inputStream.bufferedReader().use { it.readText() }.trim()
        return process.waitFor() to output
    }

    protected fun notify(project: com.intellij.openapi.project.Project, type: NotificationType, message: String) {
        NotificationGroupManager.getInstance().getNotificationGroup("Sushi").createNotification(message, type).notify(project)
    }
}

class SushiCheckAction : SushiWorkflowAction("Check") {
    override fun perform(project: com.intellij.openapi.project.Project, settings: SushiSettings.Data, input: Path, directory: Path, indicator: ProgressIndicator) {
        val (exit, output) = execute(settings, "check", input.toString(), "--target", settings.targetProfile)
        SushiToolWindow.show(project, "Check", output.ifBlank { if (exit == 0) "Sushi check passed." else "Sushi check failed." })
    }
}

class SushiRunAction : SushiWorkflowAction("Run") {
    override fun perform(project: com.intellij.openapi.project.Project, settings: SushiSettings.Data, input: Path, directory: Path, indicator: ProgressIndicator) {
        val (exit, output) = execute(settings, "run", input.toString(), "--target", settings.targetProfile)
        SushiToolWindow.show(project, "Run", output.ifBlank { if (exit == 0) "Sushi run completed." else "Sushi run failed." })
    }
}

class SushiPreviewGeneratedAction : SushiWorkflowAction("Preview Generated Output") {
    override fun perform(project: com.intellij.openapi.project.Project, settings: SushiSettings.Data, input: Path, directory: Path, indicator: ProgressIndicator) {
        val extension = if (settings.targetProfile.startsWith("powershell")) ".ps1" else if (settings.targetProfile.startsWith("zsh")) ".zsh" else ".sh"
        val generated = directory.resolve("generated$extension")
        val (exit, output) = execute(settings, "transpile", input.toString(), "--target", settings.targetProfile, "--output", generated.toString())
        if (exit != 0) { notify(project, NotificationType.ERROR, output.ifBlank { "Sushi transpilation failed." }); return }
        if (!Files.exists(generated)) { notify(project, NotificationType.ERROR, "Sushi did not produce generated output."); return }
        val file = LocalFileSystem.getInstance().refreshAndFindFileByNioFile(generated)
        if (file == null) { notify(project, NotificationType.ERROR, "Sushi did not produce generated output."); return }
        // Open beside the Sushi source, matching IntelliJ's Markdown preview split.
        FileEditorManagerEx.getInstanceEx(project).openFile(file, true, true)
    }
}

class SushiRestartLspAction : AnAction("Sushi: Restart Language Server") {
    override fun update(event: AnActionEvent) {
        event.presentation.isEnabledAndVisible = event.project != null
    }

    override fun actionPerformed(event: AnActionEvent) {
        val project = event.project ?: return
        LspClientManager.getInstance(project)
            .stopAndRestartClientsIfNeeded(SushiLspIntegrationProvider::class.java)
        NotificationGroupManager.getInstance().getNotificationGroup("Sushi")
            .createNotification("Sushi language server restarted.", NotificationType.INFORMATION)
            .notify(project)
    }
}
