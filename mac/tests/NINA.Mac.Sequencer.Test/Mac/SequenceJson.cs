#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Sequence JSON compared the way it can be: NINA re-derives the coordinates of every entity that inherits them from its
    /// target when a sequence is loaded (CoordinatesInstruction, the altitude conditions' Data, AboveHorizonCondition's Ra/Dec
    /// expressions: their OnDeserialized/AfterParentChanged write the inherited values back as expression definitions), so the
    /// RA/Dec fields of those entities change on every load, by design and on Windows as well (upstream code, unchanged; seen
    /// here as "" becoming "1.252678" or "-0", and 0.0 becoming 9.4E-17). Everything else must survive a round trip byte for byte.
    /// </summary>
    internal static class SequenceJson {
        private static readonly HashSet<string> inheritedCoordinateFields = new(StringComparer.Ordinal) {
            "Ra", "Dec", "RaDefinition", "DecDefinition", "RaExpression", "DecExpression"
        };

        public static string WithoutInheritedCoordinates(string json) {
            var token = JToken.Parse(json);
            Strip(token);
            return token.ToString(Formatting.Indented);
        }

        private static void Strip(JToken token) {
            if (token is JObject obj) {
                foreach (var property in obj.Properties().ToList()) {
                    if (inheritedCoordinateFields.Contains(property.Name)) {
                        property.Remove();
                    } else {
                        Strip(property.Value);
                    }
                }
            } else if (token is JArray array) {
                foreach (var child in array) {
                    Strip(child);
                }
            }
        }
    }
}
