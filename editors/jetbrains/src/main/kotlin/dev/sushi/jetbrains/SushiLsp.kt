package dev.sushi.jetbrains

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor
import com.intellij.lang.Language
import javax.swing.Icon

object SushiLanguage : Language("Sushi")

object SushiFileType : LanguageFileType(SushiLanguage) {
    override fun getName() = "Sushi"
    override fun getDescription() = "Sushi shell script"
    override fun getDefaultExtension() = "sushi"
    override fun getIcon(): Icon? = null
}

class SushiLspIntegrationProvider : LspIntegrationProvider {
    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension == "sushi") {
            LOG.info("Starting Sushi language server for ${file.path}")
            clientStarter.ensureClientStarted(SushiLspClientDescriptor(project))
        }
    }

    private companion object { val LOG = Logger.getInstance(SushiLspIntegrationProvider::class.java) }
}

private class SushiLspClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "Sushi") {
    override fun isSupportedFile(file: VirtualFile) = file.extension == "sushi"
    override fun createCommandLine(): GeneralCommandLine {
        val settings = SushiSettings.getInstance().state
        val command = GeneralCommandLine(settings.serverPath, "lsp", "--stdio")
        command.environment["SUSHI_LSP_TARGET"] = settings.targetProfile
        command.withWorkDirectory(project.basePath ?: ".")
        LOG.info("Launching Sushi language server: ${command.commandLineString}")
        return command
    }

    override fun getLanguageId(file: VirtualFile) = "sushi"

    private companion object { val LOG = Logger.getInstance(SushiLspClientDescriptor::class.java) }
}
