#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NINA.Mac.Siril {

    /// <summary>
    /// Quoting for Siril script words. Siril 1.4.4's tokenizer (src/core/command_line_processor.c:103-128) splits on
    /// blanks; a word that starts with ' or " runs to the next identical quote, which is removed. There is no escape
    /// character, so a quote inside a word only matters at its start, and a word holding blanks plus both quote kinds
    /// cannot be written. Options must be quoted as a whole word ("-out=../a b"), not after the '=' (research SIR-M4).
    /// </summary>
    public static class SirilQuote {

        public static string Word(string word) {
            if (string.IsNullOrEmpty(word)) {
                throw new ArgumentException("A Siril script word cannot be empty");
            }
            if (word.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) {
                throw new ArgumentException($"A Siril script word cannot contain line breaks: {word}");
            }
            var needsQuotes = word.Any(c => c == ' ' || c == '\t') || word[0] == '"' || word[0] == '\'';
            if (!needsQuotes) {
                return word;
            }
            if (!word.Contains('"')) {
                return "\"" + word + "\"";
            }
            if (!word.Contains('\'')) {
                return "'" + word + "'";
            }
            throw new ArgumentException($"Siril cannot quote a word containing blanks and both quote characters: {word}");
        }
    }

    /// <summary>Builds a Siril script line by line, quoting every argument and tracking the current directory for relative paths.</summary>
    public sealed class SirilScriptBuilder {
        private readonly StringBuilder text = new();

        public SirilScriptBuilder(string startDirectory) {
            CurrentDirectory = Path.GetFullPath(startDirectory);
        }

        /// <summary>Directory Siril will be in after the commands emitted so far.</summary>
        public string CurrentDirectory { get; private set; }

        public SirilScriptBuilder Comment(string comment) {
            foreach (var line in (comment ?? string.Empty).Split('\n')) {
                text.Append("# ").Append(line.TrimEnd('\r')).Append('\n');
            }
            return this;
        }

        public SirilScriptBuilder Blank() {
            text.Append('\n');
            return this;
        }

        public SirilScriptBuilder Command(string name, params string[] arguments) {
            text.Append(name);
            foreach (var argument in arguments.Where(a => a != null)) {
                text.Append(' ').Append(SirilQuote.Word(argument));
            }
            text.Append('\n');
            return this;
        }

        /// <summary>Emits cd with a path relative to the current directory and records the move.</summary>
        public SirilScriptBuilder Cd(string directory) {
            var full = Path.GetFullPath(directory);
            Command("cd", Relative(full));
            CurrentDirectory = full;
            return this;
        }

        /// <summary>Emits cd with an absolute path.</summary>
        public SirilScriptBuilder CdAbsolute(string directory) {
            var full = Path.GetFullPath(directory);
            Command("cd", full);
            CurrentDirectory = full;
            return this;
        }

        /// <summary>Path relative to the current directory with '/' separators ("." for the directory itself).</summary>
        public string Relative(string path) {
            var relative = Path.GetRelativePath(CurrentDirectory, Path.GetFullPath(path));
            return relative.Replace('\\', '/');
        }

        public override string ToString() => text.ToString();
    }

    /// <summary>A generated Siril script and where it runs.</summary>
    public sealed class SirilScript {

        public SirilScript(string text, string workingDirectory, string fileName, IReadOnlyList<string> warnings) {
            Text = text;
            WorkingDirectory = workingDirectory;
            FileName = fileName;
            Warnings = warnings ?? Array.Empty<string>();
        }

        public string Text { get; }

        /// <summary>Directory passed to siril-cli -d (the script also cds there itself).</summary>
        public string WorkingDirectory { get; }

        /// <summary>Suggested file name inside <see cref="WorkingDirectory"/>.</summary>
        public string FileName { get; }

        public string DefaultPath => Path.Combine(WorkingDirectory, FileName);

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Writes the script with '\n' line ends and no byte-order mark (a BOM would hide the leading 'requires').</summary>
        public string Save(string path = null) {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target)));
            File.WriteAllText(target, Text, new UTF8Encoding(false));
            return target;
        }
    }
}
