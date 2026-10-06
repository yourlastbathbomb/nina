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
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Mac.RigTools.Astronomy;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.FieldRotation;
using NINA.Mac.Sequencing.Planning;
using NINA.Sequencer.Conditions;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// The field-rotation symbols: the limit they publish reproduces MAC_PORT_PLAN.md section 6's table (bin 2, 1 px corner blur,
    /// 22.25 N), they follow the mount's reported altitude and azimuth through NINA's symbol broker, they are NaN while the mount
    /// is disconnected, and the Target form's Stop policy turns them into a LoopWhile that NINA evaluates.
    /// </summary>
    [TestFixture]
    public class FieldRotationTest {

        // MAC_PORT_PLAN.md section 6 (decision 4), reproduced by NINA.Mac.RigTools to the printed digit
        [TestCase(-30, 0, 10.6)]
        [TestCase(-30, 3, 16.6)]
        [TestCase(0, 0, 5.1)]
        [TestCase(0, 3, 28.7)]
        [TestCase(10, 0, 2.9)]
        [TestCase(10, 3, 64.7)]
        public void MaxSub_ReproducesThePlanTable(double dec, double hourAngle, double expected) {
            var horizontal = SphericalAstronomy.EquatorialToHorizontal(hourAngle, dec, 22.25);
            FieldRotationSymbols.MaxSub(22.25, horizontal.AltitudeDeg, horizontal.AzimuthDeg, 3840, 2160, 2, 1.0).Should().BeApproximately(expected, 0.05);
        }

        [Test]
        public void DueEastOrWest_TheLimitIsCapped() {
            FieldRotationSymbols.MaxSub(22.25, 40, 90, 3840, 2160, 2, 1.0).Should().Be(FieldRotationSymbols.MaxSubCapSeconds);
        }

        [Test]
        public async Task Symbols_FollowTheMountsPointing_ThroughNinasSymbolBroker() {
            var now = DateTime.Now;
            var pointing = Sky.TargetAt(60, rising: false, decDeg: 10, now);
            await using var rig = await SimRig.Create("field rotation", pointing, 0, 0, new FixedNighttimeCalculator(now.AddHours(1)));
            await rig.Host.TelescopeMediator.UnparkTelescope(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
            await rig.Host.TelescopeMediator.SlewToCoordinatesAsync(pointing, CancellationToken.None);
            var broker = rig.Host.SymbolBroker;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            double published = double.NaN;
            while (DateTime.UtcNow < deadline) {
                if (broker.TryGetValue(FieldRotationSymbols.MaxSubSymbol, out var value) && value is double d && !double.IsNaN(d)) {
                    published = d;
                    break;
                }
                await Task.Delay(200);
            }
            var info = rig.Host.TelescopeMediator.GetInfo();
            var expected = FieldRotationSymbols.MaxSub(Sky.Latitude, info.Altitude, info.Azimuth, 3840, 2160, 2, 1.0);
            TestContext.Out.WriteLine($"mount alt {info.Altitude:0.00} az {info.Azimuth:0.00}: FieldRotation_MaxSub {published:0.00} s (expected {expected:0.00} s)");
            published.Should().BeApproximately(expected, 0.5, "the camera is at bin 2 (profile), 3840 x 2160");
            broker.TryGetValue(FieldRotationSymbols.RateSymbol, out var rate).Should().BeTrue();
            ((double)rate).Should().NotBe(0);
            broker.TryGetValue(FieldRotationSymbols.OkSymbol, out var ok).Should().BeTrue();
            ok.Should().Be(1);

            // The Stop policy's LoopWhile, evaluated by NINA against the published symbol (NINA evaluates expressions only inside a sequence root)
            var root = rig.Host.Factory.GetContainer<NINA.Sequencer.Container.SequenceRootContainer>();
            var block = rig.Host.Factory.GetContainer<NINA.Sequencer.Container.SequentialContainer>();
            root.Add(block);
            var loop = rig.Host.Factory.GetCondition<LoopWhile>();
            block.Add(loop);
            loop.PredicateExpression.Definition = $"{FieldRotationSymbols.MaxSubSymbol} >= {Math.Floor(published) - 1}";
            loop.Check(null, null).Should().BeTrue();
            loop.PredicateExpression.Definition = $"{FieldRotationSymbols.MaxSubSymbol} >= {Math.Ceiling(published) + 1}";
            var check = loop.Check(null, null);
            TestContext.Out.WriteLine($"predicate '{loop.PredicateExpression.Definition}': value '{loop.PredicateExpression.ValueString}', error '{loop.PredicateExpression.Error}'");
            check.Should().BeFalse();
        }

        [Test]
        public void WhileTheMountIsDisconnected_TheSymbolsAreNaN() {
            var rig = SimRig.CreateUnconnected("field rotation disconnected");
            try {
                rig.Host.FieldRotation.UpdateDeviceInfo(new TelescopeInfo { Connected = false });
                rig.Host.SymbolBroker.TryGetValue(FieldRotationSymbols.MaxSubSymbol, out var value).Should().BeTrue("the symbol always exists, so expressions never fail on it");
                ((double)value).Should().Be(double.NaN);
                rig.Host.FieldRotation.UpdateDeviceInfo(new TelescopeInfo { Connected = true, Altitude = 45, Azimuth = 0 });
                rig.Host.SymbolBroker.TryGetValue(FieldRotationSymbols.MaxSubSymbol, out value);
                ((double)value).Should().BeApproximately(FieldRotationSymbols.MaxSub(Sky.Latitude, 45, 0, 3840, 2160, 1, 1.0), 1e-9, "camera not connected: full sensor at bin 1");
            } finally {
                rig.Host.Dispose();
            }
        }

        [Test]
        public void Bin1Symbol_DoesNotFollowTheCamerasBinning_TheLiveSymbolDoes() {
            // Review M7-1: Center's solve frames switch the camera to the plate-solve binning; the generated Stop predicate must not
            // change meaning with it
            var rig = SimRig.CreateUnconnected("field rotation bin1");
            try {
                var symbols = rig.Host.FieldRotation;
                var mount = new TelescopeInfo { Connected = true, Altitude = 62, Azimuth = 140.3 };
                double Read(string symbol) {
                    rig.Host.SymbolBroker.TryGetValue(symbol, out var value).Should().BeTrue(symbol);
                    return (double)value;
                }
                var bin1 = FieldRotationSymbols.MaxSub(Sky.Latitude, 62, 140.3, 3840, 2160, 1, 1.0);
                foreach (short binning in new short[] { 1, 2, 4 }) {
                    symbols.UpdateDeviceInfo(new CameraInfo { Connected = true, XSize = 3840, YSize = 2160, BinX = binning, BinY = binning });
                    symbols.UpdateDeviceInfo(mount);
                    Read(FieldRotationSymbols.MaxSubBin1Symbol).Should().BeApproximately(bin1, 1e-9, $"camera at bin {binning}");
                    Read(FieldRotationSymbols.MaxSubSymbol).Should().BeApproximately(bin1 * binning, 1e-9, "the live symbol is at the camera's binning");
                    symbols.MaxSubBin1Seconds.Should().BeApproximately(bin1, 1e-9);
                }
                symbols.UpdateDeviceInfo(new TelescopeInfo { Connected = false });
                double.IsNaN(Read(FieldRotationSymbols.MaxSubBin1Symbol)).Should().BeTrue("unknown while the mount is disconnected");
            } finally {
                rig.Host.Dispose();
            }
        }

        [Test]
        public void OkSymbol_JudgesThePlannedExposure() {
            var rig = SimRig.CreateUnconnected("field rotation ok");
            try {
                var symbols = rig.Host.FieldRotation;
                symbols.UpdateDeviceInfo(new TelescopeInfo { Connected = true, Altitude = 67.75, Azimuth = 180 });
                symbols.PlannedExposureSeconds = 10;
                symbols.UpdateDeviceInfo(new TelescopeInfo { Connected = true, Altitude = 67.75, Azimuth = 180 });
                rig.Host.SymbolBroker.TryGetValue(FieldRotationSymbols.OkSymbol, out var ok);
                ok.Should().Be(0, "Dec 0 at transit allows 2.5 s at bin 1 (5.1 s at bin 2), not 10 s");
                symbols.PlannedExposureSeconds = 1;
                symbols.UpdateDeviceInfo(new TelescopeInfo { Connected = true, Altitude = 67.75, Azimuth = 180 });
                rig.Host.SymbolBroker.TryGetValue(FieldRotationSymbols.OkSymbol, out ok);
                ok.Should().Be(1);
            } finally {
                rig.Host.Dispose();
            }
        }
    }
}
