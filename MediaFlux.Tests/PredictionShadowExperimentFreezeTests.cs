using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaFlux.Models;
using MediaFlux.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PredictionShadowExperimentFreezeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxFreezeTests", Guid.NewGuid().ToString("N"));
    private PredictionShadowExperimentFreezeStore Store => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void RoundTripReconstructsAllProtocolSectionsInNewStore()
    {
        var freeze = Fixture();
        Assert.False(Store.IsFrozen(freeze.ExperimentId));
        var published = Store.Create(freeze);
        var loaded = Assert.IsType<PredictionShadowExperimentFreeze>(new PredictionShadowExperimentFreezeStore(_root).Load(freeze.ExperimentId));
        Assert.Equal(JsonSerializer.Serialize(published), JsonSerializer.Serialize(loaded));
        Assert.Equal(freeze.Settings, loaded.Settings);
        Assert.Equal(freeze.Comparators, loaded.Comparators);
        Assert.Equal(freeze.AcceptanceCriteria, loaded.AcceptanceCriteria);
        Assert.Equal(freeze.Targets, loaded.Targets);
        Assert.Equal(6, loaded.Reserves.Count);
        Assert.Single(loaded.Exclusions);
        Assert.Equal(3, loaded.JournalSnapshot.Count);
        Assert.True(Store.IsFrozen(freeze.ExperimentId));
        Assert.StartsWith(Path.Combine(_root, "data", "research-experiment-freezes"), Store.GetPath(freeze.ExperimentId));
        Assert.Contains("\"SchemaVersion\": 1", File.ReadAllText(Store.GetPath(freeze.ExperimentId)));
        Assert.Contains("\"Role\": \"Target\"", File.ReadAllText(Store.GetPath(freeze.ExperimentId)));
    }

    [Fact]
    public void IdenticalLogicalFreezeDoesNotRewriteExistingArtifact()
    {
        var freeze = Fixture();
        Store.Create(freeze);
        string path = Store.GetPath(freeze.ExperimentId);
        // Whitespace/property order do not determine logical identity.
        var parsed = JsonNode.Parse(File.ReadAllText(path))!;
        var reordered = new JsonObject();
        foreach (var property in parsed.AsObject().Reverse()) reordered[property.Key] = property.Value?.DeepClone();
        File.WriteAllText(path, reordered.ToJsonString());
        byte[] before = File.ReadAllBytes(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        var equivalent = freeze with
        {
            Strata = freeze.Strata.Reverse().ToArray(),
            JournalSnapshot = freeze.JournalSnapshot.Reverse().ToArray(),
            Reserves = freeze.Reserves.Chunk(2).Reverse().SelectMany(pair => pair).ToArray(),
            ReplacementPolicy = freeze.ReplacementPolicy with { ValidReasons = freeze.ReplacementPolicy.ValidReasons.Reverse().ToArray() }
        };
        Store.Create(equivalent);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ConflictFailsClosedWithoutChangingBytes()
    {
        var freeze = Fixture();
        Store.Create(freeze);
        string path = Store.GetPath(freeze.ExperimentId);
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Store.Create(freeze with { ProtocolRevision = "changed" }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("timestamp")]
    [InlineData("commit")]
    [InlineData("version")]
    [InlineData("duplicate-slot")]
    [InlineData("unordered-slot")]
    [InlineData("duplicate-family")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-candidate")]
    [InlineData("target-count")]
    [InlineData("target-stratum-count")]
    [InlineData("target-role")]
    [InlineData("reserve-family-collision")]
    [InlineData("reserve-source-collision")]
    [InlineData("reserve-count")]
    [InlineData("reserve-order")]
    [InlineData("exclusion-collision")]
    [InlineData("invalid-stratum")]
    [InlineData("overlapping-strata")]
    [InlineData("pool-order")]
    [InlineData("missing-signature")]
    [InlineData("override")]
    [InlineData("transformation")]
    [InlineData("fps")]
    [InlineData("k")]
    [InlineData("missing-comparator")]
    [InlineData("replacement-attempt")]
    [InlineData("replacement-reasons")]
    [InlineData("invalid-criterion")]
    [InlineData("inconsistent-criterion")]
    [InlineData("support-count")]
    [InlineData("malformed-hash")]
    [InlineData("duplicate-generation")]
    [InlineData("wrong-generation-path")]
    [InlineData("missing-active-journal")]
    public void InvalidFreezeIsRejectedBeforeWriting(string change)
    {
        var f = Fixture();
        var targets = f.Targets.ToArray();
        var reserves = f.Reserves.ToArray();
        var journals = f.JournalSnapshot.ToArray();
        switch (change)
        {
            case "schema": f = f with { SchemaVersion = 2 }; break;
            case "id": f = f with { ExperimentId = " " }; break;
            case "revision": f = f with { ProtocolRevision = "" }; break;
            case "timestamp": f = f with { FrozenUtc = DateTime.SpecifyKind(f.FrozenUtc, DateTimeKind.Local) }; break;
            case "commit": f = f with { GitCommit = "short" }; break;
            case "version": f = f with { MediaFluxVersion = "" }; break;
            case "duplicate-slot": targets[1] = targets[1] with { Slot = 1 }; break;
            case "unordered-slot": (targets[0], targets[1]) = (targets[1], targets[0]); break;
            case "duplicate-family": targets[1] = targets[1] with { Source = targets[1].Source with { FamilyKey = targets[0].Source.FamilyKey } }; break;
            case "duplicate-source": targets[1] = targets[1] with { Source = targets[0].Source }; break;
            case "duplicate-candidate": targets[1] = targets[1] with { Source = targets[1].Source with { CandidateId = targets[0].Source.CandidateId } }; break;
            case "target-count": targets = targets.Skip(1).ToArray(); break;
            case "target-stratum-count": targets[0] = targets[0] with { Stratum = PredictionShadowExperimentStratum.High, OriginalPoolRank = 15 }; break;
            case "target-role": targets[0] = targets[0] with { Role = PredictionShadowExperimentRole.Replacement }; break;
            case "reserve-family-collision": reserves[0] = reserves[0] with { Source = reserves[0].Source with { FamilyKey = targets[0].Source.FamilyKey } }; break;
            case "reserve-source-collision": reserves[0] = reserves[0] with { Source = targets[0].Source }; break;
            case "reserve-count": reserves = reserves.Skip(1).ToArray(); break;
            case "reserve-order": (reserves[0], reserves[1]) = (reserves[1], reserves[0]); break;
            case "exclusion-collision": f = f with { Exclusions = [new(targets[0].Source, "excluded")] }; break;
            case "invalid-stratum": targets[0] = targets[0] with { Stratum = (PredictionShadowExperimentStratum)9 }; break;
            case "overlapping-strata": f = f with { Strata = [f.Strata[0], f.Strata[1] with { MinimumVideoBitrateBps = 9_000_000 }, f.Strata[2]] }; break;
            case "pool-order": targets[0] = targets[0] with { OriginalPoolRank = 20 }; break;
            case "missing-signature": f = f with { Settings = f.Settings with { EncoderSettingsSignature = "" } }; break;
            case "override": f = f with { Settings = f.Settings with { NoPerItemTargetSizeOverride = false } }; break;
            case "transformation": f = f with { Settings = f.Settings with { AllowScaling = true } }; break;
            case "fps": f = f with { Settings = f.Settings with { MinimumFps = double.NaN } }; break;
            case "k": f = f with { Comparators = f.Comparators with { K = 3 } }; break;
            case "missing-comparator": f = f with { Comparators = f.Comparators with { BaselineRatio = null! } }; break;
            case "replacement-attempt": f = f with { ReplacementPolicy = f.ReplacementPolicy with { ReplacementAttempt = 3 } }; break;
            case "replacement-reasons": f = f with { ReplacementPolicy = f.ReplacementPolicy with { ValidReasons = ["inaccurate prediction"] } }; break;
            case "invalid-criterion": f = f with { AcceptanceCriteria = f.AcceptanceCriteria with { MaximumTemporalDirectMedianApePercent = -1 } }; break;
            case "inconsistent-criterion": f = f with { AcceptanceCriteria = f.AcceptanceCriteria with { MaximumTemporalRatioP90ApePercent = 10 } }; break;
            case "support-count": f = f with { AcceptanceCriteria = f.AcceptanceCriteria with { MinimumJointlySupportedTemporalTargets = 4 } }; break;
            case "malformed-hash": journals[0] = journals[0] with { Sha256 = new string('Z', 64) }; break;
            case "duplicate-generation": journals[2] = journals[0]; break;
            case "wrong-generation-path": journals[2] = journals[2] with { Generation = 3 }; break;
            case "missing-active-journal": journals = journals.Where(j => j.JournalType != PredictionShadowFreezeJournalType.FinalizedStatistics).ToArray(); break;
        }
        f = f with { Targets = targets, Reserves = reserves, JournalSnapshot = journals };
        Assert.Throws<InvalidDataException>(() => Store.Create(f));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing-schema")]
    [InlineData("missing-settings")]
    [InlineData("identity")]
    [InlineData("truncated")]
    [InlineData("unknown-property")]
    [InlineData("missing-role")]
    public void CorruptOrUnsupportedStoredFreezeFailsClosed(string change)
    {
        var freeze = Fixture();
        Store.Create(freeze);
        string path = Store.GetPath(freeze.ExperimentId);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (change)
        {
            case "schema": json["SchemaVersion"] = 2; break;
            case "missing-schema": json.Remove("SchemaVersion"); break;
            case "missing-settings": json.Remove("Settings"); break;
            case "identity": json["ExperimentId"] = "another-id"; break;
            case "unknown-property": json["IgnoredProtocolField"] = true; break;
            case "missing-role": json["Targets"]![0]!.AsObject().Remove("Role"); break;
        }
        File.WriteAllText(path, change == "truncated" ? "{" : json.ToJsonString());
        byte[] corrupted = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Store.Load(freeze.ExperimentId));
        Assert.Throws<InvalidDataException>(() => Store.IsFrozen(freeze.ExperimentId));
        Assert.Throws<InvalidDataException>(() => Store.Create(freeze));
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentPublicationAndReadbackNeverExposePartialFreeze()
    {
        var freeze = Fixture();
        var creates = Enumerable.Range(0, 12).Select(_ => Task.Run(() => Store.Create(freeze))).ToArray();
        Exception? readFailure = null;
        try
        {
            for (int i = 0; i < 50; i++)
            {
                var observed = Store.Load(freeze.ExperimentId);
                if (observed is not null) Assert.Equal(24, observed.Targets.Count);
            }
        }
        catch (Exception ex)
        {
            readFailure = ex;
            throw;
        }
        finally
        {
            Task drain = Task.WhenAll(creates);
            try { await drain; }
            catch (Exception ex) when (readFailure is not null)
            {
                // Keep the primary read failure and every fault discovered while draining.
                throw new AggregateException("Freeze reads and concurrent creators failed.",
                    readFailure, (Exception?)drain.Exception ?? ex);
            }
        }
        Assert.Equal(24, Store.Load(freeze.ExperimentId)!.Targets.Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Store.GetPath(freeze.ExperimentId))!, "*.json"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Store.GetPath(freeze.ExperimentId))!, "*.tmp"));
    }

    [Fact]
    public void LoadReadsCompleteFreezeWhileRenamedFileHasAnOpenDeleteAccessHandle()
    {
        var freeze = Fixture();
        var published = Store.Create(freeze);
        string path = Store.GetPath(freeze.ExperimentId);
        string temporary = path + ".rename-test.tmp";
        File.Move(path, temporary);

        // Retain the same DELETE access used by a Windows rename after its new name is visible.
        using SafeFileHandle renameHandle = CreateFileForDelete(temporary, 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (renameHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        File.Move(temporary, path, overwrite: false);

        // Prove the incompatible sharing condition is present, then exercise the production reader.
        IOException incompatibleRead = Assert.Throws<IOException>(() => File.ReadAllBytes(path));
        Assert.Equal(32, incompatibleRead.HResult & 0xffff);
        var loaded = Assert.IsType<PredictionShadowExperimentFreeze>(Store.Load(freeze.ExperimentId));
        Assert.Equal(JsonSerializer.Serialize(published), JsonSerializer.Serialize(loaded));
        Assert.Equal(24, loaded.Targets.Count);
        Assert.True(Store.IsFrozen(freeze.ExperimentId));
        Assert.Equal(JsonSerializer.Serialize(published), JsonSerializer.Serialize(Store.Create(freeze)));
        Assert.Throws<InvalidDataException>(() => Store.Create(freeze with { ProtocolRevision = "conflict" }));
    }

    [Theory]
    [InlineData(FileAccess.Read, FileShare.None)]
    [InlineData(FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)]
    public void LoadPreservesSharingErrorsForExclusiveReadersAndIncompatibleWriters(FileAccess access, FileShare sharing)
    {
        var freeze = Fixture();
        Store.Create(freeze);
        using (var incompatible = new FileStream(Store.GetPath(freeze.ExperimentId), FileMode.Open, access, sharing))
        {
            Assert.Throws<IOException>(() => Store.Load(freeze.ExperimentId));
            Assert.Throws<IOException>(() => Store.IsFrozen(freeze.ExperimentId));
            Assert.Throws<IOException>(() => Store.Create(freeze));
        }
        Assert.Equal(24, Store.Load(freeze.ExperimentId)!.Targets.Count);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileForDelete(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    public void DuplicateJsonPropertiesFailClosedWithoutRewritingArtifact(string location)
    {
        var freeze = Fixture();
        Store.Create(freeze);
        string path = Store.GetPath(freeze.ExperimentId);
        string json = File.ReadAllText(path);
        json = location == "root"
            ? json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2, \"SchemaVersion\": 1", StringComparison.Ordinal)
            : json.Replace("\"Slot\": 1,", "\"Slot\": 99, \"Slot\": 1,", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        byte[] corrupt = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Store.Load(freeze.ExperimentId));
        Assert.Throws<InvalidDataException>(() => Store.IsFrozen(freeze.ExperimentId));
        Assert.Throws<InvalidDataException>(() => Store.Create(freeze));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentConflictingCreatorsPublishExactlyOneCompleteFreeze()
    {
        var freeze = Fixture();
        using var start = new Barrier(2);
        Task<bool> Create(string revision) => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                new PredictionShadowExperimentFreezeStore(_root).Create(freeze with { ProtocolRevision = revision });
                return true;
            }
            catch (InvalidDataException) { return false; }
        });
        bool[] results = await Task.WhenAll(Create("revision-a"), Create("revision-b"));
        Assert.Single(results, success => success);
        Assert.Single(results, success => !success);
        var loaded = Store.Load(freeze.ExperimentId)!;
        Assert.Contains(loaded.ProtocolRevision, new[] { "revision-a", "revision-b" });
        Assert.Equal(24, loaded.Targets.Count);
        string directory = Path.GetDirectoryName(Store.GetPath(freeze.ExperimentId))!;
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false, "path")]
    [InlineData(false, "family")]
    [InlineData(false, "candidate")]
    [InlineData(true, "path")]
    [InlineData(true, "family")]
    [InlineData(true, "candidate")]
    public void ExclusionCollisionChecksIdentityAndFamilyForTargetsAndReserves(bool reserve, string identity)
    {
        var freeze = Fixture();
        var rosterSource = reserve ? freeze.Reserves[0].Source : freeze.Targets[0].Source;
        var excluded = freeze.Exclusions[0].Source;
        excluded = identity switch
        {
            "path" => excluded with { SourcePath = rosterSource.SourcePath },
            "family" => excluded with { FamilyKey = rosterSource.FamilyKey },
            "candidate" => excluded with { CandidateId = rosterSource.CandidateId },
            _ => excluded
        };
        Assert.Throws<InvalidDataException>(() => Store.Create(freeze with { Exclusions = [new(excluded, "excluded fixture")] }));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void CurrentTimestampChangeFailsEvenWhenSourceLengthIsUnchanged()
    {
        var freeze = Fixture();
        Store.Create(freeze);
        var source = freeze.Targets[0].Source;
        var assignment = new PredictionShadowExperimentAssignment(freeze.ExperimentId, 1, 1, PredictionShadowExperimentStratum.Low, PredictionShadowExperimentRole.Target);
        var binding = PredictionShadowExperimentAssignmentPersistence.Capture(source.SourcePath, assignment)!;
        File.SetLastWriteTimeUtc(source.SourcePath, new DateTime(source.SourceLastWriteTimeUtcTicks, DateTimeKind.Utc).AddMinutes(1));
        Assert.Equal(source.SourceLengthBytes, new FileInfo(source.SourcePath).Length);
        Assert.Throws<InvalidDataException>(() => Store.ValidateTargetAssignment(binding, source.FamilyKey));
    }

    [Fact]
    public void ReorderedExclusionsWithSharedPathRemainLogicallyIdentical()
    {
        var freeze = Fixture();
        var source = freeze.Exclusions[0].Source;
        freeze = freeze with { Exclusions = [new(source, "teaser"), new(source, "historical source")] };
        Store.Create(freeze);
        byte[] before = File.ReadAllBytes(Store.GetPath(freeze.ExperimentId));
        Store.Create(freeze with { Exclusions = freeze.Exclusions.Reverse().ToArray() });
        Assert.Equal(before, File.ReadAllBytes(Store.GetPath(freeze.ExperimentId)));
    }

    [Fact]
    public void OrphanTemporaryFileIsNeverTreatedAsFreezeAndIdCannotTraverseDirectories()
    {
        string id = "../../outside/experiment";
        string path = Store.GetPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".interrupted.tmp", "{");
        Assert.False(Store.IsFrozen(id));
        Assert.Null(Store.Load(id));
        Assert.Equal(Path.Combine(_root, "data", "research-experiment-freezes"), Path.GetDirectoryName(path));
    }

    [Fact]
    public void NumericJournalNamingVariantsAndHashCasingReconstructDeterministically()
    {
        var freeze = Fixture();
        freeze = freeze with
        {
            GitCommit = freeze.GitCommit.ToUpperInvariant(),
            JournalSnapshot = freeze.JournalSnapshot.Concat(new[]
            {
                new PredictionShadowFreezeJournalSnapshot(PredictionShadowFreezeJournalType.ResearchShadowObservations, 2, Path.Combine(_root, "shadow.old02"), 0, new string('D', 64)),
                new PredictionShadowFreezeJournalSnapshot(PredictionShadowFreezeJournalType.ResearchShadowObservations, 0, Path.Combine(_root, "shadow.old0"), 0, new string('E', 64))
            }).ToArray()
        };
        Store.Create(freeze);
        byte[] bytes = File.ReadAllBytes(Store.GetPath(freeze.ExperimentId));
        Store.Create(freeze with { JournalSnapshot = freeze.JournalSnapshot.Reverse().ToArray(), GitCommit = freeze.GitCommit.ToLowerInvariant() });
        Assert.Equal(bytes, File.ReadAllBytes(Store.GetPath(freeze.ExperimentId)));
        Assert.Equal(5, Store.Load(freeze.ExperimentId)!.JournalSnapshot.Count);
    }

    [Fact]
    public void ExplicitAssignmentValidationUsesFreezeAndCurrentSourceIdentity()
    {
        var freeze = Fixture();
        Store.Create(freeze);
        var target = freeze.Targets[0];
        var assignment = new PredictionShadowExperimentAssignment(freeze.ExperimentId, target.Slot, 1, target.Stratum, PredictionShadowExperimentRole.Target);
        var binding = PredictionShadowExperimentAssignmentPersistence.Capture(target.Source.SourcePath, assignment)!;
        Store.ValidateTargetAssignment(binding, target.Source.FamilyKey);
        File.AppendAllText(target.Source.SourcePath, "changed");
        Assert.Throws<InvalidDataException>(() => Store.ValidateTargetAssignment(binding, target.Source.FamilyKey));
        // Freeze reconstruction is independent of whether media still exists or matches.
        Assert.Equal(24, Store.Load(freeze.ExperimentId)!.Targets.Count);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("source")]
    [InlineData("length")]
    [InlineData("timestamp")]
    [InlineData("family")]
    [InlineData("stratum")]
    [InlineData("role")]
    [InlineData("attempt")]
    [InlineData("missing-freeze")]
    [InlineData("reserve")]
    [InlineData("other-valid-member")]
    public void AssignmentMismatchIsRejected(string change)
    {
        var freeze = Fixture();
        Store.Create(freeze);
        var target = freeze.Targets[0];
        var assignment = new PredictionShadowExperimentAssignment(freeze.ExperimentId, 1, 1, target.Stratum, PredictionShadowExperimentRole.Target);
        var binding = PredictionShadowExperimentAssignmentPersistence.Capture(target.Source.SourcePath, assignment)!;
        string family = target.Source.FamilyKey;
        binding = change switch
        {
            "slot" => binding with { Assignment = assignment with { Slot = 25 } },
            "source" => binding with { SourcePath = freeze.Targets[1].Source.SourcePath },
            "length" => binding with { SourceLengthBytes = binding.SourceLengthBytes + 1 },
            "timestamp" => binding with { SourceLastWriteTimeUtcTicks = binding.SourceLastWriteTimeUtcTicks + 1 },
            "stratum" => binding with { Assignment = assignment with { Stratum = PredictionShadowExperimentStratum.High } },
            "role" => binding with { Assignment = assignment with { Role = PredictionShadowExperimentRole.Replacement } },
            "attempt" => binding with { Assignment = assignment with { Attempt = 2 } },
            "missing-freeze" => binding with { Assignment = assignment with { ExperimentId = "unfrozen" } },
            "reserve" => PredictionShadowExperimentAssignmentPersistence.Capture(freeze.Reserves[0].Source.SourcePath, assignment)!,
            "other-valid-member" => PredictionShadowExperimentAssignmentPersistence.Capture(freeze.Targets[3].Source.SourcePath, assignment)!,
            _ => binding
        };
        if (change == "family") family = "unrelated-family";
        if (change == "other-valid-member") family = freeze.Targets[3].Source.FamilyKey;
        Assert.Throws<InvalidDataException>(() => Store.ValidateTargetAssignment(binding, family));
    }

    [Fact]
    public void LegacyAssignmentStillRoundTripsAndReconstructsWithoutFreeze()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "legacy.mp4");
        File.WriteAllText(path, "legacy fixture");
        var assignment = new PredictionShadowExperimentAssignment("legacy-unfrozen", 1, 1, PredictionShadowExperimentStratum.Low, PredictionShadowExperimentRole.Target);
        var binding = PredictionShadowExperimentAssignmentPersistence.Capture(path, assignment)!;
        var item = JsonSerializer.Deserialize<EncodeQueueItemState>(JsonSerializer.Serialize(new EncodeQueueItemState { Path = path, PredictionShadowExperimentAssignment = binding }))!;
        Assert.Equal(binding, item.PredictionShadowExperimentAssignment);
        var saved = PredictionShadowExperimentAssignmentPersistence.CaptureForQueueReconstruction([(path, item.PredictionShadowExperimentAssignment)]);
        Assert.Equal(binding, PredictionShadowExperimentAssignmentPersistence.RestoreForQueueReconstruction(saved, path));
        Assert.False(Store.IsFrozen(assignment.ExperimentId));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    private PredictionShadowExperimentFreeze Fixture()
    {
        Directory.CreateDirectory(_root);
        PredictionShadowFreezeSource Source(int number)
        {
            string path = Path.Combine(_root, $"fixture-{number}.mp4");
            File.WriteAllText(path, $"synthetic fixture {number}");
            var info = new FileInfo(path);
            return new(number, path, info.Length, info.LastWriteTimeUtc.Ticks, $"fixture-family-{number}");
        }
        var strata = Enum.GetValues<PredictionShadowExperimentStratum>();
        var ranks = new int[3];
        var targets = Enumerable.Range(1, 24).Select(slot =>
        {
            var stratum = strata[(slot - 1) % 3];
            return new PredictionShadowFreezeTarget(slot, stratum, Source(slot), ++ranks[(int)stratum], PredictionShadowExperimentRole.Target);
        }).ToArray();
        var reserves = strata.SelectMany((stratum, index) => Enumerable.Range(1, 2).Select(order =>
            new PredictionShadowFreezeReserve(stratum, order, Source(25 + index * 2 + order - 1), 8 + order))).ToArray();
        return new()
        {
            SchemaVersion = 1, ExperimentId = "synthetic-gate3", ProtocolRevision = "fixture-v1",
            FrozenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), MediaFluxVersion = "1.7.3", GitCommit = new string('a', 40),
            Settings = new()
            {
                SourceCodec = "h264", SourceWidth = 1920, SourceHeight = 1080, MinimumFps = 29.97003, MaximumFps = 30,
                QualityMode = "Automatic", QualityResolutionPolicy = "Source Adaptive", QualityTarget = "Balanced", ExpectedSourceAssessment = "HighQualitySource",
                ExpectedCq = 25, Encoder = "NVENC", OutputCodec = "HEVC", Preset = "p5", BitDepth = 10,
                EncoderSettingsSignature = "fixture-signature", AutoTargetSize = true, NoPerItemTargetSizeOverride = true, NoExplicitCqOverride = true,
                ConcurrencyPolicy = "Automatic NVENC; start one selected Target at a time", AutomaticNvencConcurrency = 2, StartOneTargetAtATime = true,
                AllowScaling = false, AllowRestoration = false, AllowFilteringOrMaterialTransformation = false, TransformationRestrictions = "No timeline repair or material transformations"
            },
            Comparators = new()
            {
                K = 2, BaselineRatio = new("fixture-ratio-v1", "Fixture baseline ratio definition"), BaselineDirect = new("fixture-direct-v1", "Fixture baseline direct definition"),
                TemporalRatio = new("fixture-temporal-ratio-v1", "Fixture temporal ratio definition"), TemporalDirect = new("fixture-temporal-direct-v1", "Fixture temporal direct definition"),
                NoExtrapolation = true, NoWeighting = true, NoSpatialCorrection = true, NoFittedCoefficient = true, NoCqMixing = true,
                ChronologyAndCutoffRule = "Only finalized observations before Target FrozenUtc", FamilyIndependenceRule = "Independent source families; exclude target family",
                OtherRestrictions = "Production Historical Direct remains separate"
            },
            Strata = [new(strata[0], 7_500_000, 9_000_000), new(strata[1], 10_000_000, 12_000_000), new(strata[2], 13_000_000, 14_500_000)],
            Targets = targets, Reserves = reserves, Exclusions = [new(Source(99), "Synthetic exclusion")],
            ReplacementPolicy = new()
            {
                InitialAttempt = 1, ReplacementAttempt = 2, MaximumReplacementsPerSlot = 1, MaximumReplacementsPerStratum = 2,
                SameStratumRequired = true, ConsumeReservesInOrder = true, PreserveExperimentSlotAndStratum = true, PreserveInvalidAttemptRecords = true,
                ReplacementRole = PredictionShadowExperimentRole.Replacement,
                ValidReasons = ["cancellation", "encode failure", "validation/finalization failure", "recovered completion", "missing actual-output measurement"],
                ProhibitedOutcomeBasedReasons = ["inaccurate prediction", "Temporal range abstention", "unfavorable output", "poor compression", "hypothesis-threatening result"]
            },
            AcceptanceCriteria = new()
            {
                RequiredValidIndependentOutcomes = 24, RequiredPostBootstrapBaselineAvailability = 23,
                MinimumJointlySupportedTemporalTargets = 15, MinimumJointlySupportedTargetsPerStratum = 4, NoUnexplainedCompatibilityOrInfrastructureAbstentions = true,
                ApeDefinition = "100 * abs(predicted - actual) / actual", P90Definition = "Synthetic fixture percentile definition",
                JointlySupportedSetRule = "Same jointly supported independent targets for paired comparisons", DirectComparisonAppliesToBaselineAndTemporal = true,
                MaximumTemporalRatioMedianApePercent = 20, MaximumTemporalRatioP90ApePercent = 30, MaximumTemporalDirectMedianApePercent = 20, MaximumTemporalDirectP90ApePercent = 30,
                MinimumMeanApeImprovementPercentagePoints = 2, MinimumMeanApeImprovementRelativePercent = 10, TemporalMedianMustNotWorsen = true,
                DirectMeanMustBeStrictlyLowerThanRatio = true, DirectMedianMustBeNoHigherThanRatio = true, MaximumSupportedTemporalApePercent = 50
            },
            JournalSnapshot = [new(PredictionShadowFreezeJournalType.ResearchShadowObservations, 0, Path.Combine(_root, "shadow.jsonl"), 0, new string('a', 64)),
                new(PredictionShadowFreezeJournalType.FinalizedStatistics, 0, Path.Combine(_root, "statistics.jsonl"), 0, new string('b', 64)),
                new(PredictionShadowFreezeJournalType.ResearchShadowObservations, 2, Path.Combine(_root, "shadow.jsonl.old2"), 0, new string('c', 64))]
        };
    }
}
