using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    /// <summary>
    /// Verifies the out-of-project human approval ledger immediately before an
    /// Approved contract is consumed. Self-consistent tracked JSON is not
    /// sufficient authority.
    /// </summary>
    internal static class SpatialContractAuthorityVerifier
    {
#if UNITY_INCLUDE_TESTS
        internal static Func<string, string, string> TestOverride;
        internal static Func<string, string, SpatialContractAuthorityEvidence> TestEvidenceOverride;
#endif

        internal static bool Verify(string contractPath, string expectedContractHash, out string reason)
            => TryVerify(contractPath, expectedContractHash, out _, out reason);

        internal static bool TryVerify(
            string contractPath,
            string expectedContractHash,
            out SpatialContractAuthorityEvidence evidence,
            out string reason)
        {
            evidence = null;
            reason = null;
            var absolutePath = Path.GetFullPath(contractPath ?? string.Empty);
#if UNITY_INCLUDE_TESTS
            if (TestEvidenceOverride != null)
            {
                evidence = TestEvidenceOverride(absolutePath, expectedContractHash);
                if (evidence != null && evidence.authorized &&
                    string.Equals(evidence.contract_hash, expectedContractHash, StringComparison.OrdinalIgnoreCase)) return true;
                reason = "CONTRACT_AUTHORITY_REJECTED test authority returned a different contract snapshot.";
                return false;
            }
            if (TestOverride != null)
            {
                reason = TestOverride(absolutePath, expectedContractHash);
                if (!string.IsNullOrWhiteSpace(reason)) return false;
                evidence = new SpatialContractAuthorityEvidence
                {
                    authorized = true,
                    contract_hash = expectedContractHash
                };
                return true;
            }
#endif
            var binary = SpatialCalibrationWorkflow.ResolveUnityCtxBinary();
            if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            {
                reason = "CONTRACT_AUTHORITY_UNAVAILABLE unity-ctx is required to verify the external human-approval ledger.";
                return false;
            }
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = $"spatial verify-approved \"{EscapeArgument(absolutePath)}\" --json",
                    WorkingDirectory = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process == null)
                {
                    reason = "CONTRACT_AUTHORITY_UNAVAILABLE unity-ctx could not be started.";
                    return false;
                }
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { }
                    reason = "CONTRACT_AUTHORITY_TIMEOUT external approval verification timed out.";
                    return false;
                }
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (process.ExitCode == 0)
                {
                    var result = JsonUtility.FromJson<SpatialContractAuthorityEvidence>(stdout);
                    if (result != null && result.authorized && string.Equals(result.contract_hash, expectedContractHash, StringComparison.OrdinalIgnoreCase))
                    {
                        evidence = result;
                        return true;
                    }
                    reason = "CONTRACT_AUTHORITY_REJECTED unity-ctx verified a different contract snapshot.";
                    return false;
                }
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                reason = "CONTRACT_AUTHORITY_REJECTED " + detail.Trim();
                return false;
            }
            catch (Exception exception)
            {
                reason = "CONTRACT_AUTHORITY_UNAVAILABLE " + exception.Message;
                return false;
            }
        }

        private static string EscapeArgument(string value) => value.Replace("\"", "\\\"");
    }

    [Serializable]
    internal sealed class SpatialContractAuthorityEvidence
    {
        public bool authorized;
        public string contract_hash;
        public string contract_type;
        public string asset_guid;
        public string geometry_hash;
        public string subject_guid;
        public string target_key;
        public string subject_geometry_hash;
        public string target_geometry_hash;
    }
}
