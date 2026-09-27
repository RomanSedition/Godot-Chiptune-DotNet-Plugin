#if TOOLS
#nullable enable
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using Godot;

namespace com.IvanMurzak.Godot.MCP.Tools
{
    public partial class Tool_Editor
    {
        public const string EditorSetPluginEnabledToolId = "editor-set-plugin-enabled";

        [AiTool
        (
            EditorSetPluginEnabledToolId,
            Title = "Editor / Set Plugin Enabled",
            IdempotentHint = true
        )]
        [Description("Enable or disable an editor plugin (an addons/<name>/ folder with a plugin.cfg) by its addons/ folder name — e.g. 'wizard_dock', NOT the human-readable 'name' field inside plugin.cfg. Godot's C# hot-reload is more fragile when the edited script IS a live EditorPlugin (its dock/UI instance has to be torn down and rebuilt mid-reload); disabling the plugin before editing/rebuilding its own script, then re-enabling it once the build settles, avoids that instability. Returns the plugin's enabled state after the change.")]
        public bool SetPluginEnabled
        (
            [Description("The plugin's addons/ folder name, e.g. 'wizard_dock' (matches the identifier used in Project Settings > Plugins).")]
            string plugin,
            [Description("True to enable, false to disable.")]
            bool enabled
        )
        {
            return MainThread.Instance.Run(() =>
            {
                var ei = EditorInterface.Singleton;
                ei.SetPluginEnabled(plugin, enabled);
                return ei.IsPluginEnabled(plugin);
            });
        }
    }
}
#endif
