using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cvolo.Compiler.Tooling;
using Cvolo.Core.Packages;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Packaging;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

/// <summary>
/// An actual unsigned/verified .cvlib with Sector 1 API and Sector 5 sources,
/// installed into a temporary PackageCache, not the developer's ~/.cvolo.
/// Demonstrates that a captured package declaration survives the real Tooling
/// boundary and that opening an F12 target uses a package-owned source project.
/// Subprocess + exported JSONL remains a separate normative acceptance gate.
/// </summary>
public sealed class PackageSourceNavigationIntegrationTests
{
    [Fact]
    public void InstalledCvlib_F12CarriesPackageIdentity_AndOpeningSourceReusesPackageUniverse()
    {
        string root = Path.Combine(Path.GetTempPath(), "cvolo-lsp-package-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string packageId = "CvoloNavFixture";
            const string version = "1.0.0";
            const string declaration = "public int Ping(int value) { return value + 1; }\n";
            string source = "namespace CvoloNavFixture;\n" + declaration;
            const string consumingSource = "using CvoloNavFixture;\nint main() { return Ping(3); }\n";

            string packageSources = Path.Combine(root, "producer");
            Directory.CreateDirectory(packageSources);
            string producerPath = Path.Combine(packageSources, "Api.cvl");
            File.WriteAllText(producerPath, source);
            string cvlibPath = Path.Combine(root, packageId + ".1.0.0.cvlib");
            WriteNavPackage(cvlibPath, packageId, version, packageSources, producerPath);

            string consumer = Path.Combine(root, "consumer");
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(consumer, "App.cvlproj"), """
                <Project Sdk="Cvolo.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <PackageId>Consumer</PackageId>
                    <Version>1.0.0</Version>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="CvoloNavFixture" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);
            string mainPath = Path.Combine(consumer, "Main.cvl");
            File.WriteAllText(mainPath, consumingSource);

            var cache = new PackageCache(Path.Combine(root, "isolated-cache"));
            var installer = new PackageInstaller(cache);
            var installed = installer.InstallFromFile(cvlibPath);
            Assert.Equal(packageId, installed.PackageId);
            Assert.StartsWith(cache.RootPath, installed.OutputPath, StringComparison.OrdinalIgnoreCase);
            var hash = LocalFeed.ComputeHash(cvlibPath);
            new LockFile
            {
                Sources = [],
                Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = new(version, hash, new Dictionary<string, string>()),
                },
            }.Write(Path.Combine(consumer, "cvolo.lock.json"));

            // Compiler Tooling currently offers cache injection only via an
            // internal test hook. Reflect ONLY in the fixture setup; the
            // production LSP adapter still calls the public Tooling contract.
            var workspace = CvoloWorkspace.Create();
            MethodInfo openWithCache = Assert.Single(typeof(CvoloWorkspace)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance),
                method => method.Name == "OpenProject"
                    && method.GetParameters().Length == 3);
            var project = Assert.IsType<CvoloProject>(
                openWithCache.Invoke(workspace, [consumer, Array.Empty<string>(), cache]));
            var owner = new CvoloProjectSession(workspace, project);
            var sources = owner.Current.GetPackageSources(packageId, version);
            var packageSource = Assert.Single(sources);
            Assert.Equal(packageId, packageSource.PackageId);
            Assert.Equal(version, packageSource.Version);
            Assert.Equal("Api.cvl", packageSource.RelativePath);
            Assert.Equal(source, packageSource.Source);
            Assert.True(File.Exists(packageSource.FilePath));
            Assert.StartsWith(consumer, packageSource.FilePath, StringComparison.OrdinalIgnoreCase);

            var backend = new CvoloLanguageBackend([consumer], null, new RecordingCoreLogger());
            FieldInfo field = typeof(CvoloLanguageBackend).GetField(
                "_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sessions = Assert.IsType<ConcurrentDictionary<string, CvoloProjectSession>>(
                field.GetValue(backend));
            Assert.True(sessions.TryAdd("project:" + Path.GetFullPath(consumer), owner));

            var mainUri = DocumentUri.Create(mainPath);
            Assert.Same(owner, backend.OpenProject(mainUri));
            Assert.True(backend.TryResolveDocument(owner, mainUri, out BackendDocumentHandle handle));
            var snapshot = backend.CaptureCurrentSnapshot(owner);
            BackendSymbolInfo? symbol = backend.GetSymbolAtPosition(
                snapshot, handle, consumingSource.IndexOf("Ping(3)", StringComparison.Ordinal) + 1);
            Assert.NotNull(symbol);
            var definitions = backend.GetDefinitions(snapshot, symbol!.Symbol);
            var target = Assert.Single(definitions.Targets);
            Assert.Equal(BackendDefinitionSourceKind.PackageSource, target.SourceKind);
            Assert.Equal(packageId, target.PackageId);
            Assert.Equal(version, target.PackageVersion);
            Assert.Equal(packageSource.FilePath, target.Document.LocalPath);
            Assert.Equal(source, definitions.DocumentTexts[target.Document]);
            Assert.Equal("Ping", source.Substring(target.SelectionSpan.Start, target.SelectionSpan.Length));

            // This is precisely what a client does after receiving the URI in
            // textDocument/definition and sending didOpen for that package file.
            var packageSession = Assert.IsType<CvoloProjectSession>(backend.OpenProject(target.Document));
            Assert.True(packageSession.IsPackageSource);
            Assert.NotSame(owner, packageSession);
            Assert.Same(packageSession, backend.OpenProject(target.Document));
            Assert.True(backend.TryResolveDocument(packageSession, target.Document, out var packageHandle));
            backend.UpdateDocument(packageSession, packageHandle, source);
            var pkgDoc = packageSession.Current.GetDocument(((CvoloDocumentHandle)packageHandle).DocumentId);
            Assert.Equal(source, pkgDoc.Text.ToString());
            backend.RestoreBaseline(packageSession, packageHandle);
            Assert.True(backend.TryResolveDocument(owner, mainUri, out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteNavPackage(string path, string id, string version, string root, string sourcePath)
    {
        string manifest = JsonSerializer.Serialize(new
        {
            Format = "cvlib.slice-manifest.v1",
            PackageId = id,
            Version = version,
            Dependencies = Array.Empty<object>(),
            Slices = new[]
            {
                new
                {
                    Triple = TargetTriple.HostTriple(),
                    Sector2 = new { Offset = 0UL, Length = 0UL },
                    Sector3 = new { Offset = 0UL, Length = 1UL },
                },
            },
        });
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest);
        byte[] api = new PackageApiMetadata
        {
            Units =
            [
                new PackageApiUnit
                {
                    Namespace = id,
                    Functions =
                    [
                        new PackageApiFunction
                        {
                            Name = "Ping", ReturnType = "int",
                            Parameters = [new PackageApiParameter("int", "value")],
                        },
                    ],
                },
            ],
        }.Serialize();
        var sector1 = new byte[4 + manifestBytes.Length + api.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
        manifestBytes.CopyTo(sector1, 4);
        api.CopyTo(sector1, 4 + manifestBytes.Length);
        var archive = new CvlArchiveWriter();
        archive.SetSector(1, sector1);
        archive.SetSector(3, new byte[] { 0 });
        archive.SetSourceBuffer(PackageSourceBundle.Build([sourcePath], root));
        archive.WriteUnsigned(path);
    }
}
