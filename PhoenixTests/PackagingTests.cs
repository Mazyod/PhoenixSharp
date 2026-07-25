using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public sealed class PackagingTests
    {
        [Test]
        public void UnityCompileSafetyNetIsWiredIntoTestBuildTest()
        {
            var repositoryRoot = FindRepositoryRoot();
            var compileFixtureDirectory = Path.Combine(
                repositoryRoot,
                "PhoenixUnityCompile"
            );
            var coreProjectPath = Path.Combine(
                compileFixtureDirectory,
                "Core",
                "Phoenix.UnityCompile.Core.csproj"
            );
            var unityProjectPath = Path.Combine(
                compileFixtureDirectory,
                "Unity",
                "Phoenix.UnityCompile.Unity.csproj"
            );

            Assert.Multiple(() =>
            {
                Assert.That(
                    File.Exists(coreProjectPath),
                    Is.True
                );
                Assert.That(
                    File.Exists(unityProjectPath),
                    Is.True
                );
            });

            var testsProject = XDocument.Load(
                Path.Combine(repositoryRoot, "PhoenixTests", "PhoenixTests.csproj")
            );
            var coreProject = XDocument.Load(coreProjectPath);
            var unityProject = XDocument.Load(unityProjectPath);
            var compileProjectReference = FindItem(
                testsProject,
                "ProjectReference",
                "Phoenix.UnityCompile.Unity.csproj"
            );
            var coreRuntimeGlob = FindItem(
                coreProject,
                "Compile",
                "Runtime/**/*.cs"
            );
            var unityRuntimeGlob = FindItem(
                unityProject,
                "Compile",
                "Runtime/Unity/**/*.cs"
            );

            Assert.Multiple(() =>
            {
                Assert.That(
                    compileProjectReference?.Attribute(
                        "ReferenceOutputAssembly"
                    )?.Value,
                    Is.EqualTo("false").IgnoreCase
                );
                Assert.That(
                    GetProperty(coreProject, "DefineConstants"),
                    Does.Contain("UNITY_5_3_OR_NEWER")
                );
                Assert.That(
                    GetProperty(coreProject, "EnableDefaultCompileItems"),
                    Is.EqualTo("false").IgnoreCase
                );
                Assert.That(
                    coreRuntimeGlob?.Attribute("Exclude")?.Value,
                    Does.Contain("Runtime/Unity/**/*.cs")
                );
                Assert.That(
                    GetProperty(unityProject, "DefineConstants"),
                    Does.Contain("UNITY_5_3_OR_NEWER")
                );
                Assert.That(
                    GetProperty(unityProject, "EnableDefaultCompileItems"),
                    Is.EqualTo("false").IgnoreCase
                );
                Assert.That(unityRuntimeGlob, Is.Not.Null);
                Assert.That(
                    FindItem(
                        unityProject,
                        "Compile",
                        "UnityEngineStub.cs"
                    ),
                    Is.Not.Null
                );
                Assert.That(
                    FindItem(
                        unityProject,
                        "ProjectReference",
                        "Phoenix.UnityCompile.Core.csproj"
                    ),
                    Is.Not.Null
                );
            });
        }

        [Test]
        public void NuGetPackagingMetadataEnablesDocsAndSourceDebuggingTest()
        {
            var repositoryRoot = FindRepositoryRoot();
            var project = XDocument.Load(
                Path.Combine(repositoryRoot, "Phoenix", "Phoenix.csproj")
            );
            var sourceLinkReference = FindItem(
                project,
                "PackageReference",
                "Microsoft.SourceLink.GitHub"
            );

            Assert.Multiple(() =>
            {
                Assert.That(
                    GetProperty(project, "GenerateDocumentationFile"),
                    Is.EqualTo("true").IgnoreCase
                );
                Assert.That(
                    GetProperty(project, "NoWarn")
                        .Split(';')
                        .Select(value => value.Trim()),
                    Does.Contain("1591")
                );
                Assert.That(
                    GetProperty(project, "PublishRepositoryUrl"),
                    Is.EqualTo("true").IgnoreCase
                );
                Assert.That(
                    GetProperty(project, "EmbedUntrackedSources"),
                    Is.EqualTo("true").IgnoreCase
                );
                Assert.That(
                    GetProperty(project, "IncludeSymbols"),
                    Is.EqualTo("true").IgnoreCase
                );
                Assert.That(
                    GetProperty(project, "SymbolPackageFormat"),
                    Is.EqualTo("snupkg")
                );
                Assert.That(
                    GetProperty(project, "Deterministic"),
                    Is.EqualTo("true").IgnoreCase
                );
                // MSBuild does NOT trim property values, and the SDK gates
                // DeterministicSourcePaths on the EXACT string "true" - a
                // multi-line element value silently disables the feature.
                Assert.That(
                    GetProperty(project, "ContinuousIntegrationBuild"),
                    Is.EqualTo("true")
                );
                Assert.That(sourceLinkReference, Is.Not.Null);
                Assert.That(
                    sourceLinkReference?.Attribute("Version")?.Value,
                    Is.EqualTo("8.0.0")
                );
                Assert.That(
                    sourceLinkReference?.Attribute("PrivateAssets")?.Value,
                    Is.EqualTo("All").IgnoreCase
                );
            });
        }

        private static XElement? FindItem(
            XDocument project,
            string itemName,
            string includeSuffix
        )
        {
            return project
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == itemName
                    && element.Attribute("Include")?.Value
                        .Replace('\\', '/')
                        .EndsWith(
                            includeSuffix,
                            StringComparison.Ordinal
                        ) == true
                );
        }

        private static string GetProperty(
            XDocument project,
            string propertyName
        )
        {
            return project
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == propertyName
                )
                ?.Value
                ?? string.Empty;
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(
                    TestContext.CurrentContext.TestDirectory
                );
                directory != null;
                directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(directory.FullName, "Phoenix.sln")
                    ))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException(
                "Could not locate the PhoenixSharp repository root."
            );
        }
    }
}
