using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class McpBridgeSetupWindow : EditorWindow
    {
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/Concept Room Decorator/MCP Bridge Setup")]
        public static void Open() => GetWindow<McpBridgeSetupWindow>("MCP Bridge Setup");

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Codex and Claude Code MCP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This copies the dependency-free local MCP bridge into the project and creates project-scoped configuration. Unity must be open while an agent uses the tools. No MCP tool can Apply a preview.", MessageType.Info);
            EditorGUILayout.LabelField("Unity host", McpBridgeHost.IsRunning ? $"Running on 127.0.0.1:{McpBridgeHost.Port}" : "Stopped");
            EditorGUILayout.SelectableLabel(McpBridgeHost.SessionFilePath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (GUILayout.Button("Install / Refresh Project MCP Bridge", GUILayout.Height(30f)))
            {
                try
                {
                    Install();
                    status = "Installed the bridge and generated Codex / Claude Code configuration.";
                }
                catch (Exception exception)
                {
                    status = exception.Message;
                    Debug.LogException(exception);
                }
            }

            if (!string.IsNullOrWhiteSpace(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("After setup", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Codex: trust this project, then restart the Codex client.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Claude Code: approve the project-scoped .mcp.json server when prompted.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        private static void Install()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpBridgeSetupWindow).Assembly);
            if (package == null) throw new InvalidOperationException("Could not resolve the installed package path.");
            var source = Path.Combine(package.resolvedPath, "Tools~", "McpBridge", "server.mjs");
            if (!File.Exists(source)) throw new FileNotFoundException("The packaged MCP bridge is missing.", source);

            var bridgeDirectory = Path.Combine(projectRoot, ".dungeon-decorator", "mcp");
            Directory.CreateDirectory(bridgeDirectory);
            var target = Path.Combine(bridgeDirectory, "server.mjs");
            File.Copy(source, target, true);

            WriteCodexConfig(projectRoot, target);
            WriteClaudeConfig(projectRoot, target);
            AssetDatabase.Refresh();
        }

        private static void WriteCodexConfig(string projectRoot, string bridgePath)
        {
            var configDirectory = Path.Combine(projectRoot, ".codex");
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "config.toml");
            var normalizedBridge = Normalize(bridgePath);
            var normalizedProject = Normalize(projectRoot);
            var block = $"[mcp_servers.unity_concept_room]\ncommand = \"node\"\nargs = [\"{normalizedBridge}\", \"--project\", \"{normalizedProject}\"]\n";

            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, block, new UTF8Encoding(false));
                return;
            }

            var existing = File.ReadAllText(configPath);
            if (existing.Contains("[mcp_servers.unity_concept_room]")) return;
            File.AppendAllText(configPath, $"\n{block}", new UTF8Encoding(false));
        }

        private static void WriteClaudeConfig(string projectRoot, string bridgePath)
        {
            var configPath = Path.Combine(projectRoot, ".mcp.json");
            var normalizedBridge = Normalize(bridgePath).Replace("\\", "\\\\");
            var normalizedProject = Normalize(projectRoot).Replace("\\", "\\\\");
            var json = "{\n" +
                       "  \"mcpServers\": {\n" +
                       "    \"unity-concept-room\": {\n" +
                       "      \"type\": \"stdio\",\n" +
                       "      \"command\": \"node\",\n" +
                       $"      \"args\": [\"{normalizedBridge}\", \"--project\", \"{normalizedProject}\"]\n" +
                       "    }\n" +
                       "  }\n" +
                       "}\n";

            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, json, new UTF8Encoding(false));
                return;
            }

            var existing = File.ReadAllText(configPath);
            if (existing.Contains("unity-concept-room")) return;
            var snippetPath = Path.Combine(projectRoot, ".dungeon-decorator", "claude-mcp.snippet.json");
            File.WriteAllText(snippetPath, json, new UTF8Encoding(false));
            Debug.LogWarning($"Existing .mcp.json was preserved. Merge the generated snippet manually: {snippetPath}");
        }

        private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
    }
}
