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
using Moq;
using NINA.Core.Model;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Sequencer.SequenceItem.Utility;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// NINA's ExternalScript item on macOS (the end-of-night Siril run, decision 8): upstream's own tests use Windows' mshta.exe, so
    /// these run the same paths with /bin/sh scripts: a quoted path with spaces, the exit-code symbol, failure on a non-zero exit,
    /// and NINA symbols expanded into the command line before it runs.
    /// </summary>
    [TestFixture]
    public class ExternalScriptMacTest {
        private SimRig rig = null!;

        [OneTimeSetUp]
        public void Create() {
            rig = SimRig.CreateUnconnected("external script");
        }

        [OneTimeTearDown]
        public void Dispose() {
            rig.Host.Dispose();
        }

        private string Script(string name, string body) {
            var path = Path.Combine(rig.Folder, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        private ExternalScript Item(string commandLine) {
            var root = rig.Host.Factory.GetContainer<NINA.Sequencer.Container.SequenceRootContainer>();
            var item = rig.Host.Factory.GetItem<ExternalScript>();
            root.Add(item);
            item.Script = commandLine;
            return item;
        }

        private int LastExitCode() {
            rig.Host.SymbolBroker.TryGetValue("NINA_LastExternalScriptExitCode", out var value).Should().BeTrue();
            return Convert.ToInt32(value);
        }

        [Test]
        public async Task AScriptWithSpacesInItsPath_RunsWithItsArguments_AndSetsTheExitCodeSymbol() {
            var output = Path.Combine(rig.Folder, "ran.txt");
            var script = Script("siril stand in.sh", $"printf '%s|%s' \"$1\" \"$2\" > '{output}'");
            var item = Item($"\"{script}\" first \"second arg\"");
            item.Validate().Should().BeTrue(string.Join(" | ", item.Issues));

            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            File.ReadAllText(output).Should().Be("first|second arg");
            LastExitCode().Should().Be(0);
        }

        [Test]
        public async Task ANonZeroExit_FailsTheItem_AndIsInTheSymbol() {
            var item = Item($"\"{Script("fails.sh", "exit 3")}\"");
            await item.Awaiting(i => i.Execute(new Progress<ApplicationStatus>(), CancellationToken.None)).Should().ThrowAsync<SequenceEntityFailedException>();
            LastExitCode().Should().Be(3);
        }

        [Test]
        public void AMissingProgram_IsAValidationIssue() {
            var item = Item("/opt/homebrew/bin/no-such-siril-cli -s night.ssf");
            item.Validate().Should().BeFalse();
            item.Issues.Should().ContainSingle();
        }

        [Test]
        public async Task NinaSymbols_AreExpandedBeforeTheCommandRuns() {
            var output = Path.Combine(rig.Folder, "symbols.txt");
            var item = Item($"\"{Script("symbols.sh", $"printf '%s' \"$1\" > '{output}'")}\" {{1 + 2}}");
            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            File.ReadAllText(output).Should().Be("3");
        }
    }
}
