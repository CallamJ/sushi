package dev.sushi.jetbrains

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.options.SearchableConfigurable
import com.intellij.ui.components.JBTextField
import com.intellij.util.ui.FormBuilder
import javax.swing.JComponent

@State(name = "SushiSettings", storages = [Storage("sushi.xml")])
class SushiSettings : PersistentStateComponent<SushiSettings.Data> {
    data class Data(var serverPath: String = "sushi", var targetProfile: String = "auto")
    private var data = Data()
    override fun getState() = data
    override fun loadState(state: Data) { data = state }
    companion object { fun getInstance(): SushiSettings = ApplicationManager.getApplication().getService(SushiSettings::class.java) }
}

class SushiConfigurable : SearchableConfigurable {
    private val executable = JBTextField()
    private val target = JBTextField()
    override fun getId() = "dev.sushi.settings"
    override fun getDisplayName() = "Sushi"
    override fun createComponent(): JComponent = FormBuilder.createFormBuilder()
        .addLabeledComponent("Sushi executable:", executable)
        .addLabeledComponent("Target profile:", target)
        .addComponentFillVertically(javax.swing.JPanel(), 0)
        .panel
    override fun isModified(): Boolean { val state = SushiSettings.getInstance().state; return executable.text != state.serverPath || target.text != state.targetProfile }
    override fun apply() { SushiSettings.getInstance().loadState(SushiSettings.Data(executable.text.ifBlank { "sushi" }, target.text.ifBlank { "auto" })) }
    override fun reset() { val state = SushiSettings.getInstance().state; executable.text = state.serverPath; target.text = state.targetProfile }
}
