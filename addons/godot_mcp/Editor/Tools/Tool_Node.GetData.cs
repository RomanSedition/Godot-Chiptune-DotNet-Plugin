#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using com.IvanMurzak.Godot.MCP.Data;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using Godot;

namespace com.IvanMurzak.Godot.MCP.Tools
{
    public partial class Tool_Node
    {
        public const string NodeGetPropertyToolId = "node-get-property";

        [AiTool
        (
            NodeGetPropertyToolId,
            Title = "Node / Get Property",
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = false
        )]
        [Description("Read one or more property values off a Node in the currently edited Godot scene by " +
            "name, via Godot's dynamic Object.Get — the same property names used by 'node-modify' path " +
            "patches (e.g. 'position', 'size', 'color', 'shape'). This is the read counterpart to " +
            "'node-modify', for verifying values a user has set in the Inspector rather than assuming they " +
            "match what was asked for. Returns each property's Godot string form (e.g. a Vector2 as " +
            "'(0, 500)', a Color as '(0.4, 0.25, 0.15, 1)'). A property name may use '/' to reach into a " +
            "Resource-typed property's own properties, e.g. 'shape/size' for a CollisionShape2D's assigned " +
            "shape — do this instead of round-tripping a raw instance id through resource-get-data, since a " +
            "64-bit Godot instance id can silently lose precision through JSON. A property that still holds " +
            "a Resource at the end of the path (nothing further requested into it) is returned as " +
            "'<ClassName>#<instanceId>' for reference only. An unknown property name, or a path segment that " +
            "isn't a Resource/Object, comes back as 'null' rather than an error.")]
        public Dictionary<string, string> GetProperty
        (
            [Description("Reference to the Node to read (instanceId preferred, else scene-tree path).")]
            NodeRef nodeRef,
            [Description("Property names to read, e.g. ['position', 'size', 'color', 'shape/size'].")]
            string[] propertyNames
        )
        {
            if (nodeRef == null)
                throw new ArgumentNullException(nameof(nodeRef));
            if (!nodeRef.IsValid(out var validationError))
                throw new ArgumentException(validationError, nameof(nodeRef));
            if (propertyNames == null || propertyNames.Length == 0)
                throw new ArgumentException("propertyNames must not be empty.", nameof(propertyNames));

            return MainThread.Instance.Run(() =>
            {
                var node = ResolveNode(nodeRef, out var error);
                if (node == null)
                    throw new Exception(error ?? $"Node by {nodeRef} not found.");

                var result = new Dictionary<string, string>();
                foreach (var propPath in propertyNames)
                {
                    var segments = propPath.Split('/');

                    GodotObject? current = node;
                    Variant variant = default;
                    for (var i = 0; i < segments.Length && current != null; i++)
                    {
                        variant = current.Get(segments[i]);
                        current = variant.VariantType == Variant.Type.Object ? variant.AsGodotObject() : null;
                    }

                    if (variant.VariantType == Variant.Type.Object)
                    {
                        var obj = variant.AsGodotObject();
                        result[propPath] = obj == null ? "null" : $"{obj.GetClass()}#{obj.GetInstanceId()}";
                    }
                    else
                    {
                        result[propPath] = variant.ToString();
                    }
                }
                return result;
            });
        }
    }
}
#endif
