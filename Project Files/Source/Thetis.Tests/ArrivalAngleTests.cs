/*  ArrivalAngleTests.cs

Copyright (C) 2026 Daniel Poznanski, 3Z9AM

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.
*/

using System;
using Xunit;

namespace Thetis.Tests
{
    public class ArrivalAngleTests
    {
        [Fact]
        public void HorizontalHalfWave_RoundTrip()
        {
            // D = lambda/2: no ambiguity, el 30 deg from the RX2 side -> dphi = pi*cos(30)
            var el = DiversityForm.ArrivalElevations(Math.PI * Math.Cos(30 * Math.PI / 180), 0.5, false);
            Assert.Single(el);
            Assert.Equal(30, el[0].Item1, 6);
            Assert.True(el[0].Item2);
            // from the RX1 side the phase flips
            el = DiversityForm.ArrivalElevations(-Math.PI * Math.Cos(30 * Math.PI / 180), 0.5, false);
            Assert.Equal(30, el[0].Item1, 6);
            Assert.False(el[0].Item2);
        }

        [Fact]
        public void Vertical_RoundTrip()
        {
            // RX2 0.25 lambda above RX1, el 20 deg -> dphi = pi/2*sin(20)
            var el = DiversityForm.ArrivalElevations(Math.PI / 2 * Math.Sin(20 * Math.PI / 180), 0.25, true);
            Assert.Single(el);
            Assert.Equal(20, el[0].Item1, 6);
        }

        [Fact]
        public void HVRatio_RoundTrip80m()
        {
            // G5RV 22 m, vertical 20 m from 5 m: on 80 m V-H falls with elevation, one answer
            double m = DiversityForm.HVRatioDb(25, 3.7e6, 22, 5, 20);
            var el = DiversityForm.ElevationsFromHVRatio(m, 3.7e6, 22, 5, 20, out bool exact);
            Assert.True(exact);
            Assert.Single(el);
            Assert.Equal(25, el[0], 0);
            Assert.True(DiversityForm.HVRatioDb(5, 3.7e6, 22, 5, 20) > DiversityForm.HVRatioDb(60, 3.7e6, 22, 5, 20));
        }

        [Fact]
        public void LongBaseline_ListsEveryAmbiguity()
        {
            // D = 2 lambda: 2*pi wraps, the true 40 deg is among the candidates
            double dphi = Math.IEEERemainder(4 * Math.PI * Math.Sin(40 * Math.PI / 180), 2 * Math.PI);
            var el = DiversityForm.ArrivalElevations(dphi, 2, true);
            Assert.True(el.Count > 1);
            Assert.Contains(el, e => Math.Abs(e.Item1 - 40) < 1e-6);
        }
    }
}
