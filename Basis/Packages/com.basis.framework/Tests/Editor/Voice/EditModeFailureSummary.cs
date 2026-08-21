using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

/// <summary>
/// Temporary validation aid: reports failed CLI EditMode tests together at the end of the run so
/// the remote validator's tail-only log reader can expose failures that happened early in the suite.
/// </summary>
[InitializeOnLoad]
internal static class EditModeFailureSummary
{
    private static readonly FailureCallbacks Callbacks = new FailureCallbacks();
    private static TestRunnerApi api;

    static EditModeFailureSummary()
    {
        EditorApplication.delayCall += Register;
    }

    private static void Register()
    {
        api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(Callbacks);
    }

    private sealed class FailureCallbacks : ICallbacks
    {
        private readonly List<string> failures = new List<string>();

        public void RunStarted(ITestAdaptor testsToRun)
        {
            failures.Clear();
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            var summary = new StringBuilder();
            summary.AppendLine("=== EDITMODE FAILURE SUMMARY ===");
            summary.AppendLine($"failed={result.FailCount} passed={result.PassCount} skipped={result.SkipCount}");
            for (int i = 0; i < failures.Count; i++)
            {
                summary.AppendLine(failures[i]);
            }
            summary.AppendLine("=== END EDITMODE FAILURE SUMMARY ===");
            Debug.Log(summary.ToString());
        }

        public void TestStarted(ITestAdaptor test) { }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite || result.TestStatus != TestStatus.Failed)
            {
                return;
            }

            failures.Add($"FAIL: {result.Test.FullName}\nMESSAGE: {result.Message}\nSTACK: {result.StackTrace}");
        }
    }
}
