﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using NuGet.Versioning;
using OmniSharp.DotNetTest.Models;
using OmniSharp.DotNetTest.TestFrameworks;
using OmniSharp.Eventing;
using OmniSharp.Services;

namespace OmniSharp.DotNetTest
{
    internal class VSTestManager : TestManager
    {
        public VSTestManager(Project project, string workingDirectory, DotNetCliService dotNetCli, SemanticVersion dotNetCliVersion, IEventEmitter eventEmitter, ILoggerFactory loggerFactory)
            : base(project, workingDirectory, dotNetCli, dotNetCliVersion, eventEmitter, loggerFactory.CreateLogger<VSTestManager>())
        {
        }

        private object GetDefaultRunSettings(string targetFrameworkVersion)
        {
            if (!string.IsNullOrWhiteSpace(targetFrameworkVersion))
            {
                return $@"
<RunSettings>
    <RunConfiguration>
        <TargetFrameworkVersion>{targetFrameworkVersion}</TargetFrameworkVersion>
    </RunConfiguration>
</RunSettings>";
            }

            return "<RunSettings/>";
        }

        protected override string GetCliTestArguments(int port, int parentProcessId)
        {
            return $"vstest --Port:{port} --ParentProcessId:{parentProcessId}";
        }

        protected override void VersionCheck()
        {
            SendMessage(MessageType.VersionCheck, 1);

            var message = ReadMessage();
            var version = message.DeserializePayload<int>();

            if (version != 1)
            {
                throw new InvalidOperationException($"Expected ProtocolVersion 1, but was {version}");
            }
        }

        protected override bool PrepareToConnect()
        {
            // The project must be built before we can test.
            var arguments = "build";

            // If this is .NET CLI version 2.0.0 or greater, we also specify --no-restore to ensure that
            // implicit restore on build doesn't slow the build down.
            if (DotNetCliVersion >= new SemanticVersion(2, 0, 0))
            {
                arguments += " --no-restore";
            }

            var process = DotNetCli.Start(arguments, WorkingDirectory);

            process.OutputDataReceived += (_, e) =>
            {
                EmitTestMessage(TestMessageLevel.Informational, e.Data ?? string.Empty);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                EmitTestMessage(TestMessageLevel.Error, e.Data ?? string.Empty);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            return process.ExitCode == 0
                && File.Exists(Project.OutputFilePath);
        }

        private static void VerifyTestFramework(string testFrameworkName)
        {
            var testFramework = TestFramework.GetFramework(testFrameworkName);
            if (testFramework == null)
            {
                throw new InvalidOperationException($"Unknown test framework: {testFrameworkName}");
            }
        }

        public override GetTestStartInfoResponse GetTestStartInfo(string methodName, string testFrameworkName, string targetFrameworkVersion)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = DiscoverTests(methodName, targetFrameworkVersion);

            SendMessage(MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new
                {
                    TestCases = testCases,
                    DebuggingEnabled = true,
                    RunSettings = GetDefaultRunSettings(targetFrameworkVersion)
                });

            var message = ReadMessage();
            var testStartInfo = message.DeserializePayload<TestProcessStartInfo>();

            return new GetTestStartInfoResponse
            {
                Executable = testStartInfo.FileName,
                Argument = testStartInfo.Arguments,
                WorkingDirectory = testStartInfo.WorkingDirectory
            };
        }

        public override async Task<DebugTestGetStartInfoResponse> DebugGetStartInfoAsync(string methodName, string testFrameworkName, string targetFrameworkVersion, CancellationToken cancellationToken)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = await DiscoverTestsAsync(methodName, targetFrameworkVersion, cancellationToken);

            SendMessage(MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new
                {
                    TestCases = testCases,
                    DebuggingEnabled = true,
                    RunSettings = GetDefaultRunSettings(targetFrameworkVersion)
                });

            var message = await ReadMessageAsync(cancellationToken);
            var startInfo = message.DeserializePayload<TestProcessStartInfo>();

            return new DebugTestGetStartInfoResponse
            {
                FileName = startInfo.FileName,
                Arguments = startInfo.Arguments,
                WorkingDirectory = startInfo.WorkingDirectory,
                EnvironmentVariables = startInfo.EnvironmentVariables
            };
        }

        public override async Task DebugLaunchAsync(CancellationToken cancellationToken)
        {
            SendMessage(MessageType.CustomTestHostLaunchCallback,
                new
                {
                    HostProcessId = Process.GetCurrentProcess().Id
                });

            var done = false;

            while (!done)
            {
                var (success, message) = await TryReadMessageAsync(cancellationToken);
                if (!success)
                {
                    break;
                }

                switch (message.MessageType)
                {
                    case MessageType.TestMessage:
                        EmitTestMessage(message.DeserializePayload<TestMessagePayload>());
                        break;

                    case MessageType.ExecutionComplete:
                        done = true;
                        break;
                }
            }
        }

        public override RunTestResponse RunTest(string methodName, string testFrameworkName, string targetFrameworkVersion)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = DiscoverTests(methodName, targetFrameworkVersion);

            var testResults = new List<TestResult>();

            if (testCases.Length > 0)
            {
                // Now, run the tests.
                SendMessage(MessageType.TestRunSelectedTestCasesDefaultHost,
                    new
                    {
                        TestCases = testCases,
                        RunSettings = GetDefaultRunSettings(targetFrameworkVersion)
                    });

                var done = false;

                while (!done)
                {
                    var message = ReadMessage();

                    switch (message.MessageType)
                    {
                        case MessageType.TestMessage:
                            EmitTestMessage(message.DeserializePayload<TestMessagePayload>());
                            break;

                        case MessageType.TestRunStatsChange:
                            var testRunChange = message.DeserializePayload<TestRunChangedEventArgs>();

                            testResults.AddRange(testRunChange.NewTestResults);
                            break;

                        case MessageType.ExecutionComplete:
                            var payload = message.DeserializePayload<TestRunCompletePayload>();

                            done = true;
                            break;
                    }
                }
            }

            var results = testResults.Select(testResult =>
                new DotNetTestResult
                {
                    MethodName = testResult.TestCase.FullyQualifiedName,
                    Outcome = testResult.Outcome.ToString().ToLowerInvariant(),
                    ErrorMessage = testResult.ErrorMessage,
                    ErrorStackTrace = testResult.ErrorStackTrace
                });

            return new RunTestResponse
            {
                Results = results.ToArray(),
                Pass = !testResults.Any(r => r.Outcome == TestOutcome.Failed)
            };
        }

        private async Task<TestCase[]> DiscoverTestsAsync(string methodName, string targetFrameworkVersion, CancellationToken cancellationToken)
        {
            SendMessage(MessageType.StartDiscovery,
                new
                {
                    Sources = new[]
                    {
                        Project.OutputFilePath
                    },
                    RunSettings = GetDefaultRunSettings(targetFrameworkVersion)
                });

            var testCases = new List<TestCase>();
            var done = false;

            while (!done)
            {
                var (success, message) = await TryReadMessageAsync(cancellationToken);
                if (!success)
                {
                    return Array.Empty<TestCase>();
                }

                switch (message.MessageType)
                {
                    case MessageType.TestMessage:
                        EmitTestMessage(message.DeserializePayload<TestMessagePayload>());
                        break;

                    case MessageType.TestCasesFound:
                        foreach (var testCase in message.DeserializePayload<TestCase[]>())
                        {
                            var testName = testCase.FullyQualifiedName;

                            var testNameEnd = testName.IndexOf('(');
                            if (testNameEnd >= 0)
                            {
                                testName = testName.Substring(0, testNameEnd);
                            }

                            testName = testName.Trim();

                            if (testName.Equals(methodName, StringComparison.Ordinal))
                            {
                                testCases.Add(testCase);
                            }
                        }

                        break;

                    case MessageType.DiscoveryComplete:
                        done = true;
                        break;
                }
            }

            return testCases.ToArray();
        }

        private TestCase[] DiscoverTests(string methodName, string targetFrameworkVersion)
        {
            return DiscoverTestsAsync(methodName, targetFrameworkVersion, CancellationToken.None).Result;
        }
    }
}
