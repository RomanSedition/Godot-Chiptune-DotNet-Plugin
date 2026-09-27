---
name: editor-set-plugin-enabled
description: Enable or disable an editor plugin (an addons/<name>/ folder with a plugin.cfg) by its addons/ folder name — e.g. 'wizard_dock', NOT the human-readable 'name' field inside plugin.cfg. Godot's C# hot-reload is more fragile when the edited script IS a live EditorPlugin (its dock/UI instance has to be torn down and rebuilt mid-reload); disabling the plugin before editing/rebuilding its own script, then re-enabling it once the build settles, avoids that instability. Returns the plugin's enabled state after the change.
---

# Editor / Set Plugin Enabled

Enable or disable an editor plugin (an addons/<name>/ folder with a plugin.cfg) by its addons/ folder name — e.g. 'wizard_dock', NOT the human-readable 'name' field inside plugin.cfg. Godot's C# hot-reload is more fragile when the edited script IS a live EditorPlugin (its dock/UI instance has to be torn down and rebuilt mid-reload); disabling the plugin before editing/rebuilding its own script, then re-enabling it once the build settles, avoids that instability. Returns the plugin's enabled state after the change.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:29649/api/tools/editor-set-plugin-enabled \
  -H "Content-Type: application/json" \
  -d '{
  "plugin": "string_value",
  "enabled": false
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:29649/api/tools/editor-set-plugin-enabled -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:29649/api/tools/editor-set-plugin-enabled \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "plugin": "string_value",
  "enabled": false
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `plugin` | `string` | Yes | The plugin's addons/ folder name, e.g. 'wizard_dock' (matches the identifier used in Project Settings > Plugins). |
| `enabled` | `boolean` | Yes | True to enable, false to disable. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "plugin": {
      "type": "string",
      "description": "The plugin's addons/ folder name, e.g. 'wizard_dock' (matches the identifier used in Project Settings > Plugins)."
    },
    "enabled": {
      "type": "boolean",
      "description": "True to enable, false to disable."
    }
  },
  "required": [
    "plugin",
    "enabled"
  ]
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "type": "boolean"
    }
  },
  "required": [
    "result"
  ]
}
```

