#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem;
using NUnit.Framework.Interfaces;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Merge guards for the headless sequencer build. An upstream merge that changes a file the mac build copies, trims or anchors
    /// would otherwise build and pass while the mac side silently keeps the old state:
    /// <list type="bullet">
    /// <item>the two trimmed Editing copies and the reduced IFramingAssistantVM pin a hash of their upstream file (copyright region
    /// excluded, LF): when one fails, re-sync the mac copy, then update the pin;</item>
    /// <item>each namespace anchor (NINA.Sequencer.Mac/Mac/NamespaceAnchors.cs) is still needed, i.e. its upstream using line still
    /// exists; when one fails, delete the anchor;</item>
    /// <item>the fork seam in CenterAfterDriftTrigger.cs (upstream edit) is still there;</item>
    /// <item>the left-out folders stay out of the assembly, and the assembly keeps upstream's identity.</item>
    /// </list>
    /// NINA.Mac.Engine.Test's UpstreamParityTest additionally compares NINA.Sequencer.Mac.csproj's packages and upstream Compile
    /// Remove items with NINA.Sequencer.csproj (it declares UpstreamDir).
    /// </summary>
    [TestFixture]
    public class SequencerParityTest {

        private static string Root => TestHost.NinaRoot;

        private static readonly (string Upstream, string Copy, string Sha256)[] copies = {
            ("NINA.Sequencer/Editing/SequenceEditContext.cs", "mac/src/NINA.Sequencer.Mac/MacReplacements/Editing/SequenceEditContext.cs",
                "825659c4e7af605615d9ef023d0b38fd4d00ff263e1bd6ee1877ffe16b2fbf85"),
            ("NINA.Sequencer/Editing/SequencePropertyCapture.cs", "mac/src/NINA.Sequencer.Mac/MacReplacements/Editing/SequencePropertyCapture.cs",
                "60435dd030fc13defe6a765e449c0717224fca2537fbaefb9277d9d066b6a508"),
            ("NINA.WPF.Base/Interfaces/ViewModel/IFramingAssistantVM.cs", "mac/src/NINA.WPF.Base.Mac/Replaced/IFramingAssistantVM.cs",
                "b110d02d81e66f654549084bea09bac76c061b187fa23116e195e2facc2b6bc0"),
        };

        [Test]
        public void CopiedUpstreamFiles_AreUnchangedSinceTheMacCopyWasSynced() {
            var stale = new List<string>();
            foreach (var (upstream, copy, pinned) in copies) {
                var actual = ContentHash(Path.Combine(Root, upstream));
                if (actual != pinned) {
                    stale.Add($"upstream {upstream} changed (sha256 {actual}): re-sync {copy}, then update the pin in {nameof(SequencerParityTest)}");
                }
                File.Exists(Path.Combine(Root, copy)).Should().BeTrue(copy);
            }
            stale.Should().BeEmpty();
        }

        [Test]
        public void TrimmedEditingCopies_KeepEveryUpstreamMemberTheyDoNotDocumentAsRemoved() {
            var context = File.ReadAllText(Path.Combine(Root, copies[0].Copy));
            foreach (var member in new[] { "CreateCommand", "SelfRecordingCommand", "ExecuteCommand", "Register(", "Unregister(", "Find(", "Placement(", "Structure(", "Target(", "Property<T>(", "Toggle(" }) {
                context.Should().Contain(member);
            }
            foreach (var removed in new[] { "DependencyProperty", "StepExpression(", "class HistoryCommand", "GetHistory(" }) {
                context.Should().NotContain(removed);
            }
            var capture = File.ReadAllText(Path.Combine(Root, copies[1].Copy));
            capture.Should().Contain("Capture<T>(").And.Contain("Target(ISequenceEntity owner)").And.NotContain("BindingExpression binding)");
        }

        [Test]
        public void ReducedFramingAssistant_KeepsUpstreamsSetCoordinatesSignature() {
            File.ReadAllText(Path.Combine(Root, "NINA.WPF.Base/Interfaces/ViewModel/IFramingAssistantVM.cs")).Should().Contain("Task<bool> SetCoordinates(DeepSkyObject dso);");
            File.ReadAllText(Path.Combine(Root, copies[2].Copy)).Should().Contain("Task<bool> SetCoordinates(DeepSkyObject dso);");
        }

        public static IEnumerable<TestCaseData> Anchors() {
            yield return new TestCaseData("NINA.Sequencer/Sequencer.cs", "using NINA.Sequencer.SequenceItem.Autofocus;");
            yield return new TestCaseData("NINA.Sequencer/Sequencer.cs", "using NINA.Sequencer.SequenceItem.Focuser;");
            yield return new TestCaseData("NINA.Sequencer/Sequencer.cs", "using NINA.Sequencer.Trigger.MeridianFlip;");
            yield return new TestCaseData("NINA.Sequencer/Trigger/Guider/DitherAfterExposures.cs", "using ASCOM.Com.DriverAccess;");
            yield return new TestCaseData("NINA.Sequencer/Trigger/Platesolving/CenterAfterDriftTrigger.cs", "using ASCOM.Com.DriverAccess;");
            yield return new TestCaseData("NINA.Sequencer/SequenceItem/Utility/WaitForSunAltitude.cs", "using Nikon;");
            yield return new TestCaseData("NINA.Sequencer/Conditions/MoonIlluminationCondition.cs", "using NINA.Equipment.Equipment.MyGuider.SkyGuard.SkyGuardMessages;");
            yield return new TestCaseData("NINA.Sequencer/Logic/SymbolBroker.cs", "using System.Windows.Documents;");
        }

        [TestCaseSource(nameof(Anchors))]
        public void NamespaceAnchor_IsStillNeeded(string upstreamFile, string usingLine) {
            File.ReadAllLines(Path.Combine(Root, upstreamFile)).Select(l => l.Trim()).Should().Contain(usingLine,
                $"{upstreamFile} no longer imports the namespace: delete its anchor in mac/src/NINA.Sequencer.Mac/Mac/NamespaceAnchors.cs");
            var ns = usingLine["using ".Length..].TrimEnd(';');
            File.ReadAllText(Path.Combine(Root, "mac/src/NINA.Sequencer.Mac/Mac/NamespaceAnchors.cs")).Should().Contain($"namespace {ns} {{");
        }

        [Test]
        public void ForkSeam_InCenterAfterDriftTrigger_IsStillThere() {
            var text = File.ReadAllText(Path.Combine(Root, "NINA.Sequencer/Trigger/Platesolving/CenterAfterDriftTrigger.cs"));
            text.Should().Contain("CenteringPlateSolverFactory ?? new PlateSolverFactoryProxy(), CenteringWindowServiceFactory ?? new WindowServiceFactory()");
            var type = typeof(NINA.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger);
            type.GetProperty("CenteringPlateSolverFactory", BindingFlags.Instance | BindingFlags.NonPublic).Should().NotBeNull("internal, not public API");
            type.GetProperty("CenteringWindowServiceFactory", BindingFlags.Instance | BindingFlags.NonPublic).Should().NotBeNull();
            type.GetConstructors().Should().ContainSingle(c => c.IsPublic && c.GetParameters().Length == 11, "the MEF constructor is unchanged");
        }

        [Test]
        public void LeftOutFolders_StayOutOfTheAssembly() {
            var namespaces = typeof(ISequenceItem).Assembly.GetTypes().Select(t => t.Namespace).Where(n => n != null).Distinct().ToList();
            foreach (var left in new[] { "NINA.Sequencer.Trigger.MeridianFlip", "NINA.Sequencer.SequenceItem.Autofocus", "NINA.Sequencer.Trigger.Autofocus",
                "NINA.Sequencer.SequenceItem.Dome", "NINA.Sequencer.Trigger.Dome", "NINA.Sequencer.SequenceItem.Rotator", "NINA.Sequencer.SequenceItem.Switch",
                "NINA.Sequencer.SequenceItem.SafetyMonitor", "NINA.Sequencer.Trigger.SafetyMonitor", "NINA.Sequencer.SequenceItem.Focuser", "NINA.Sequencer.View",
                "NINA.Sequencer.Behaviors" }) {
                // Anchored namespaces exist but hold only the internal anchor class
                typeof(ISequenceItem).Assembly.GetTypes().Where(t => t.Namespace == left && t.Name != "MacNamespaceAnchor").Should().BeEmpty(left);
            }
            namespaces.Should().Contain("NINA.Sequencer.Trigger.Platesolving");
        }

        [Test]
        public void Assembly_KeepsUpstreamsIdentity_SoSavedSequencesResolve() {
            var assembly = typeof(ISequenceItem).Assembly;
            assembly.GetName().Name.Should().Be("NINA.Sequencer");
            assembly.GetName().Version.Should().Be(typeof(NINA.Core.Utility.CoreUtil).Assembly.GetName().Version);
            assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Should().Be("NINA.Sequencer");
            typeof(NINA.WPF.Base.ViewModel.BaseVM).Assembly.GetName().Name.Should().Be("NINA.WPF.Base");
            typeof(NINA.Sequencer.Generators.UsesExpressionsAttribute).Assembly.GetName().Name.Should().Be("NINA.Sequencer.Generators");
        }

        [Test]
        public void NinaAppSettingsStandIn_MatchesUpstreamsSaveQueueDefault() {
            var settings = XDocument.Load(Path.Combine(Root, "NINA/Properties/Settings.settings"));
            var value = settings.Descendants().Where(e => e.Name.LocalName == "Setting" && (string?)e.Attribute("Name") == "SaveQueueSize")
                .Single().Descendants().Single(e => e.Name.LocalName == "Value").Value;
            value.Should().Be("2", "mac/src/NINA.Mac.Sequencing/Mac/NinaAppStandIns.cs hard-codes upstream's default");
        }

        [Test]
        public void WpfBaseIncludeList_StillMatchesUpstreamFiles() {
            var csproj = XDocument.Load(Path.Combine(Root, "mac/src/NINA.WPF.Base.Mac/NINA.WPF.Base.Mac.csproj"));
            var includes = csproj.Descendants("Compile").Select(e => (string?)e.Attribute("Include")).Where(i => i != null && i.Contains("$(WpfBaseUpstreamDir)")).ToList();
            includes.Should().HaveCountGreaterThan(10);
            foreach (var include in includes) {
                var relative = include!.Replace("$(WpfBaseUpstreamDir)", "");
                var directory = Path.Combine(Root, "NINA.WPF.Base", Path.GetDirectoryName(relative)!);
                Directory.GetFiles(directory, Path.GetFileName(relative)).Should().NotBeEmpty(include);
            }
        }

        private static string ContentHash(string path) {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n");
            text = Regex.Replace(text, "^(ï»¿)?#region \"copyright\".*?#endregion \"copyright\"\n*", "", RegexOptions.Singleline);
            return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text))).ToLowerInvariant();
        }
    }

    /// <summary>Every entry of the skip list names a linked upstream test that exists, and gives a reason.</summary>
    [TestFixture]
    public class MacPlatformSkipsTest {

        [Test]
        public void EverySkippedTest_Exists_AndHasAReason() {
            var methods = typeof(SequencerParityTest).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttributes().Any(a => a is TestAttribute || a is TestCaseAttribute || a is TestCaseSourceAttribute))
                .Select(m => t.FullName + "." + m.Name))
                .ToHashSet();
            foreach (var (test, reason) in MacPlatformSkipsAttribute.Listed) {
                methods.Should().Contain(test);
                reason.Should().NotBeNullOrWhiteSpace();
            }
            MacPlatformSkipsAttribute.Listed.Should().HaveCount(8);
        }
    }
}
