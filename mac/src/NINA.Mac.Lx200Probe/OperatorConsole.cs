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

namespace NINA.Mac.Lx200Probe {

    /// <summary>Where prompts go and answers come from. Injected so the checklist and raw console are testable.</summary>
    public interface IOperatorConsole {

        void WriteLine(string text);

        void Write(string text);

        /// <summary>Next line typed by the operator, or null at end of input.</summary>
        string ReadLine();
    }

    public sealed class SystemConsole : IOperatorConsole {

        public void WriteLine(string text) => Console.WriteLine(text);

        public void Write(string text) => Console.Write(text);

        public string ReadLine() => Console.ReadLine();
    }

    /// <summary>Scripted answers for tests; records everything written.</summary>
    public sealed class ScriptedConsole : IOperatorConsole {
        private readonly Queue<string> answers;
        private readonly List<string> output = new();

        public ScriptedConsole(params string[] answers) {
            this.answers = new Queue<string>(answers ?? Array.Empty<string>());
        }

        /// <summary>Called before each answer is returned; tests use it to cancel or throw at a given prompt.</summary>
        public Action<string> OnRead { get; set; }

        public string LastPrompt { get; private set; }

        public IReadOnlyList<string> Output => output;

        public string Text => string.Join("\n", output);

        public void WriteLine(string text) {
            output.Add(text);
            LastPrompt = text;
        }

        public void Write(string text) {
            output.Add(text);
            LastPrompt = text;
        }

        public string ReadLine() {
            OnRead?.Invoke(LastPrompt);
            return answers.Count > 0 ? answers.Dequeue() : null;
        }
    }

    /// <summary>Questions to the operator. With <see cref="AutoYes"/> (simulator only) confirmations answer themselves.</summary>
    public sealed class Prompter {

        public Prompter(IOperatorConsole console, bool autoYes = false) {
            Console = console;
            AutoYes = autoYes;
        }

        public IOperatorConsole Console { get; }

        public bool AutoYes { get; }

        public void Say(string text) => Console.WriteLine(text);

        /// <summary>[y/N]; anything but y/yes (or end of input) is no.</summary>
        public bool Confirm(string question) {
            Console.WriteLine(question);
            if (AutoYes) {
                Console.WriteLine("  > y (auto: simulator run)");
                return true;
            }
            Console.Write("  Proceed? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            return answer is "y" or "yes";
        }

        /// <summary>The operator must type <paramref name="word"/> exactly.</summary>
        public bool ConfirmStrong(string question, string word) {
            Console.WriteLine(question);
            if (AutoYes) {
                Console.WriteLine($"  > {word} (auto: simulator run)");
                return true;
            }
            Console.Write($"  Type {word} to continue, anything else skips: ");
            return string.Equals(Console.ReadLine()?.Trim(), word, StringComparison.Ordinal);
        }

        /// <summary>Free-text observation; empty when skipped or in auto mode.</summary>
        public string Ask(string question) {
            Console.Write(question + " ");
            if (AutoYes) {
                Console.WriteLine("(auto: no operator)");
                return "";
            }
            return Console.ReadLine()?.Trim() ?? "";
        }
    }
}
