#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NINA.Mac.App.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Views {

    public partial class TargetView : UserControl {

        public TargetView() {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
            base.OnAttachedToVisualTree(e);
            Attach();
        }

        protected override void OnDataContextChanged(System.EventArgs e) {
            base.OnDataContextChanged(e);
            Attach();
        }

        /// <summary>Gives the horizon editor the window's file pickers (import/export .hrz).</summary>
        private void Attach() {
            if (DataContext is TargetViewModel vm && TopLevel.GetTopLevel(this) is { } top) {
                vm.Horizon.Dialogs = new StorageDialogs(top.StorageProvider);
            }
        }

        private sealed class StorageDialogs : IFileDialogs {
            private readonly IStorageProvider storage;

            public StorageDialogs(IStorageProvider storage) {
                this.storage = storage;
            }

            public async Task<string> PickOpenAsync(string title, string extension) {
                if (!storage.CanOpen) {
                    return null;
                }
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType> {
                        new($"Horizon (*.{extension})") { Patterns = new[] { $"*.{extension}", "*.txt" } },
                        FilePickerFileTypes.All,
                    },
                });
                return files?.FirstOrDefault()?.TryGetLocalPath();
            }

            public async Task<string> PickSaveAsync(string title, string suggestedName, string extension) {
                if (!storage.CanSave) {
                    return null;
                }
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
                    Title = title,
                    SuggestedFileName = suggestedName,
                    DefaultExtension = extension,
                });
                return file?.TryGetLocalPath();
            }
        }
    }
}
