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
using NINA.Core.Enum;
using NINA.PlateSolving;
using NINA.PlateSolving.Mac;
using NINA.PlateSolving.Solvers;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>The rig's plate-solve settings on a real NINA profile, and what NINA's own factory then builds from them.</summary>
    [TestFixture]
    [NonParallelizable]
    public class RigPlateSolveDefaultsTest {

        private int savedCpuLimit;

        [SetUp]
        public void SaveCpuLimit() {
            savedCpuLimit = AstrometryNetSetup.CpuLimitSeconds;
        }

        [TearDown]
        public void RestoreCpuLimit() {
            // Apply sets astrometry.net's process-wide CPU limit
            AstrometryNetSetup.CpuLimitSeconds = savedCpuLimit;
        }

        [Test]
        public void NinasDefaults_OnMacOS_HaveNoAstapPath_AndAnAstapBlindSolver() {
            // Why a mac profile needs these settings: upstream's ASTAP default (%programfiles%\astap\astap.exe) never exists here
            var settings = new NINA.Profile.Profile("defaults").PlateSolveSettings;

            settings.ASTAPLocation.Should().BeEmpty();
            settings.PlateSolverType.Should().Be(PlateSolverEnum.ASTAP);
            settings.BlindSolverType.Should().Be(BlindSolverEnum.ASTAP, "upstream's blind solver is ASTAP's -r 180 all-sky search");
            settings.DownSampleFactor.Should().Be(0);
            settings.SearchRadius.Should().Be(30);
            settings.ExposureTime.Should().Be(2);
        }

        [Test]
        public void Apply_SetsTheRigsSolverChoiceAndSolveFrames_AndKeepsTheRest() {
            var settings = new NINA.Profile.Profile("rig").PlateSolveSettings;
            settings.Threshold = 0.75;
            settings.MaxObjects = 300;

            RigPlateSolveDefaults.Apply(settings, "/data/Solvers/astap");

            settings.PlateSolverType.Should().Be(PlateSolverEnum.ASTAP);
            settings.ASTAPLocation.Should().Be("/data/Solvers/astap");
            settings.BlindSolverType.Should().Be(BlindSolverEnum.LOCAL);
            settings.CygwinLocation.Should().Be("/opt/homebrew/bin");
            settings.BlindFailoverEnabled.Should().BeTrue();
            settings.DownSampleFactor.Should().Be(2);
            settings.SearchRadius.Should().Be(5);
            settings.ExposureTime.Should().Be(15);
            settings.Gain.Should().Be(450);
            settings.Binning.Should().Be(2);
            settings.Threshold.Should().Be(0.75);
            settings.MaxObjects.Should().Be(300);

            PlateSolverFactory.GetPlateSolver(settings).Should().BeOfType<ASTAPSolver>();
            PlateSolverFactory.GetBlindSolver(settings).Should().BeOfType<LocalPlateSolver>()
                .Which.SolveFieldPath.Should().Be("/opt/homebrew/bin/solve-field");
        }

        [Test]
        public void Apply_BoundsAFailingCentringStep_ToUnderTenMinutes() {
            // Review PS-2: with NINA's defaults (10 attempts, 2 minutes apart) and solve-field's cpulimit 300, the blind failover
            // made a centring step under cloud take over 80 minutes before it gave up
            var settings = new NINA.Profile.Profile("rig").PlateSolveSettings;
            AstrometryNetSetup.CpuLimitSeconds = 300;
            settings.NumberOfAttempts.Should().Be(10, "NINA's default");
            settings.ReattemptDelay.Should().Be(2, "NINA's default, minutes");
            settings.ExposureTime = RigPlateSolveDefaults.ExposureTime;
            settings.BlindFailoverEnabled = true;
            settings.BlindSolverType = BlindSolverEnum.LOCAL;
            RigPlateSolveDefaults.WorstCaseFailingCentringStep(settings).Should().Be(TimeSpan.FromSeconds(10 * (15 + 360) + 9 * 120));

            RigPlateSolveDefaults.Apply(settings, "/data/Solvers/astap");

            settings.NumberOfAttempts.Should().Be(3);
            settings.ReattemptDelay.Should().Be(1);
            AstrometryNetSetup.CpuLimitSeconds.Should().Be(60);
            AstrometryNetSetup.SolverTimeout.Should().Be(TimeSpan.FromSeconds(120));
            RigPlateSolveDefaults.WorstCaseFailingCentringStep(settings).Should().Be(TimeSpan.FromSeconds(3 * (15 + 120) + 2 * 60));
            RigPlateSolveDefaults.WorstCaseFailingCentringStep(settings).Should().BeLessThan(TimeSpan.FromMinutes(10));
        }
    }
}
