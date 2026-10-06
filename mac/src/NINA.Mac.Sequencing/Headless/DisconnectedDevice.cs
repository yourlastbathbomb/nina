#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace NINA.Mac.Sequencing.Headless {

    /// <summary>
    /// The handler a device mediator gets for a device this rig does not have (filter wheel, rotator, dome, flat panel, switch,
    /// safety monitor, weather, and the focuser until one is connected). NINA's mediators need a registered handler even for an
    /// absent device: without one, DeviceMediator.GetInfo() returns null and, for example, Center dereferences
    /// domeMediator.GetInfo().Connected (plan section 4.2, rule 1).
    /// <para>
    /// One generic implementation serves every device view-model interface (IFilterWheelVM, IDomeVM, ...), built with
    /// <see cref="DispatchProxy"/>, so a member upstream adds to one of those interfaces needs no change here. Every call answers
    /// as a disconnected device does: GetDeviceInfo returns a fresh info object with Connected = false
    /// (<see cref="DeviceInfo.CreateDefaultInstance{T}"/>), Connect returns false, Rescan an empty list, GetDevice null; other
    /// Task-returning members complete at once with the default result (false, null, 0); events are accepted and never raised;
    /// properties and other members return the type's default (null, false, 0). Nothing throws.
    /// </para>
    /// </summary>
    public static class DisconnectedDevice {

        /// <summary>A disconnected handler implementing <typeparamref name="TViewModel"/> whose GetDeviceInfo returns <typeparamref name="TInfo"/>.</summary>
        public static TViewModel Create<TViewModel, TInfo>() where TViewModel : class where TInfo : DeviceInfo, new() {
            var proxy = DispatchProxy.Create<TViewModel, Handler<TInfo>>();
            return proxy;
        }

        public class Handler<TInfo> : DispatchProxy where TInfo : DeviceInfo, new() {

            protected override object Invoke(MethodInfo targetMethod, object[] args) {
                if (targetMethod == null) {
                    return null;
                }
                var returnType = targetMethod.ReturnType;
                switch (targetMethod.Name) {
                    case "GetDeviceInfo":
                        return DeviceInfo.CreateDefaultInstance<TInfo>();
                    case "Rescan":
                        return Task.FromResult<IList<string>>(new List<string>());
                }
                if (returnType == typeof(void)) {
                    return null;
                }
                if (returnType == typeof(Task)) {
                    return Task.CompletedTask;
                }
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)) {
                    var result = Default(returnType.GetGenericArguments()[0]);
                    return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, new[] { result });
                }
                return Default(returnType);
            }

            private static object Default(Type type) {
                if (type == typeof(DeviceInfo) || typeof(DeviceInfo).IsAssignableFrom(type) && type == typeof(TInfo)) {
                    return DeviceInfo.CreateDefaultInstance<TInfo>();
                }
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
        }
    }
}
