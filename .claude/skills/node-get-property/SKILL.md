---
name: node-get-property
description: Read one or more property values off a Node in the currently edited Godot scene by name, via Godot's dynamic Object.Get — the same property names used by 'node-modify' path patches (e.g. 'position', 'size', 'color', 'shape'). This is the read counterpart to 'node-modify', for verifying values a user has set in the Inspector rather than assuming they match what was asked for. Returns each property's Godot string form (e.g. a Vector2 as '(0, 500)', a Color as '(0.4, 0.25, 0.15, 1)'). A property name may use '/' to reach into a Resource-typed property's own properties, e.g. 'shape/size' for a CollisionShape2D's assigned shape — do this instead of round-tripping a raw instance id through resource-get-data, since a 64-bit Godot instance id can silently lose precision through JSON. A property that still holds a Resource at the end of the path (nothing further requested into it) is returned as '<ClassName>#<instanceId>' for reference only. An unknown property name, or a path segment that isn't a Resource/Object,…
---

# Node / Get Property

Read one or more property values off a Node in the currently edited Godot scene by name, via Godot's dynamic Object.Get — the same property names used by 'node-modify' path patches (e.g. 'position', 'size', 'color', 'shape'). This is the read counterpart to 'node-modify', for verifying values a user has set in the Inspector rather than assuming they match what was asked for. Returns each property's Godot string form (e.g. a Vector2 as '(0, 500)', a Color as '(0.4, 0.25, 0.15, 1)'). A property name may use '/' to reach into a Resource-typed property's own properties, e.g. 'shape/size' for a CollisionShape2D's assigned shape — do this instead of round-tripping a raw instance id through resource-get-data, since a 64-bit Godot instance id can silently lose precision through JSON. A property that still holds a Resource at the end of the path (nothing further requested into it) is returned as '<ClassName>#<instanceId>' for reference only. An unknown property name, or a path segment that isn't a Resource/Object, comes back as 'null' rather than an error.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:29649/api/tools/node-get-property \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "propertyNames": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:29649/api/tools/node-get-property -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:29649/api/tools/node-get-property \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "propertyNames": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the Node to read (instanceId preferred, else scene-tree path). |
| `propertyNames` | `any` | Yes | Property names to read, e.g. ['position', 'size', 'color', 'shape/size']. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the Node to read (instanceId preferred, else scene-tree path)."
    },
    "propertyNames": {
      "$ref": "#/$defs/System.String-1",
      "description": "Property names to read, e.g. ['position', 'size', 'color', 'shape/size']."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.NodeRef": {
      "type": "object",
      "properties": {
        "instanceId": {
          "type": "integer",
          "description": "Instance id of the Node (Godot GodotObject.GetInstanceId()). If '0', treated as unset. Priority: 1."
        },
        "path": {
          "type": "string",
          "description": "Scene-tree path of the Node, e.g. '/root/Main/Player' or 'Main/Player'. Priority: 2."
        }
      },
      "required": [
        "instanceId"
      ],
      "description": "Reference to a Godot Node in the scene tree, located by scene-tree path or instance id."
    },
    "System.String-1": {
      "type": "array",
      "items": {
        "type": "string"
      }
    }
  },
  "required": [
    "nodeRef",
    "propertyNames"
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
      "$ref": "#/$defs/System.Collections.Generic.Dictionary(System.String,System.String)"
    }
  },
  "$defs": {
    "System.Collections.Generic.Dictionary(System.String,System.String)": {
      "type": "object",
      "additionalProperties": {
        "type": "string"
      }
    }
  },
  "required": [
    "result"
  ]
}
```

