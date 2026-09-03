using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Session;
using Jellyfin.Api.WebSocketListeners;
using Jellyfin.Database.Implementations;
using Jellyfin.Drawing;
using Jellyfin.Drawing.NetVips;
using Jellyfin.Drawing.Skia;
using Jellyfin.LiveTv;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Devices;
using Jellyfin.Server.Implementations.Events;
using Jellyfin.Server.Implementations.Extensions;
using Jellyfin.Server.Implementations.Security;
using Jellyfin.Server.Implementations.Trickplay;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Providers.Lyric;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server
{
    /// <summary>
    /// Implementation of the abstract <see cref="ApplicationHost" /> class.
    /// </summary>
    public class CoreAppHost : ApplicationHost
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CoreAppHost" /> class.
        /// </summary>
        /// <param name="applicationPaths">The <see cref="ServerApplicationPaths" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="options">The <see cref="StartupOptions" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="startupConfig">The <see cref="IConfiguration" /> to be used by the <see cref="CoreAppHost" />.</param>
        public CoreAppHost(
            IServerApplicationPaths applicationPaths,
            ILoggerFactory loggerFactory,
            IStartupOptions options,
            IConfiguration startupConfig)
            : base(
                applicationPaths,
                loggerFactory,
                options,
                startupConfig)
        {
        }

        /// <inheritdoc/>
        protected override void RegisterServices(IServiceCollection serviceCollection)
        {
            // Register an image encoder
            serviceCollection.AddSingleton(typeof(IImageEncoder), SelectImageEncoder());

            serviceCollection.AddEventServices();
            serviceCollection.AddSingleton<IBaseItemManager, BaseItemManager>();
            serviceCollection.AddSingleton<IEventManager, EventManager>();

            serviceCollection.AddSingleton<IActivityManager, ActivityManager>();
            serviceCollection.AddSingleton<IUserManager, UserManager>();
            serviceCollection.AddSingleton<IAuthenticationProvider, DefaultAuthenticationProvider>();
            serviceCollection.AddSingleton<IAuthenticationProvider, InvalidAuthProvider>();
            serviceCollection.AddSingleton<IPasswordResetProvider, DefaultPasswordResetProvider>();
            serviceCollection.AddSingleton<IDisplayPreferencesManager, DisplayPreferencesManager>();
            serviceCollection.AddSingleton<IDeviceManager, DeviceManager>();
            serviceCollection.AddSingleton<ITrickplayManager, TrickplayManager>();

            // TODO search the assemblies instead of adding them manually?
            serviceCollection.AddSingleton<IWebSocketListener, SessionWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, ActivityLogWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, ScheduledTasksWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, SessionInfoWebSocketListener>();

            serviceCollection.AddSingleton<IAuthorizationContext, AuthorizationContext>();

            serviceCollection.AddScoped<IAuthenticationManager, AuthenticationManager>();

            foreach (var type in GetExportTypes<ILyricProvider>())
            {
                serviceCollection.AddSingleton(typeof(ILyricProvider), type);
            }

            foreach (var type in GetExportTypes<ILyricParser>())
            {
                serviceCollection.AddSingleton(typeof(ILyricParser), type);
            }

            base.RegisterServices(serviceCollection);
        }

        /// <summary>
        /// Picks the image encoder to register, honouring <see cref="ServerConfiguration.ImageEncoder"/>.
        /// </summary>
        /// <remarks>
        /// A missing native library is not fatal: the server falls back to the next usable encoder and
        /// logs why, so a bad configuration cannot leave it unable to start.
        /// </remarks>
        /// <returns>The <see cref="IImageEncoder"/> implementation to use.</returns>
        private Type SelectImageEncoder()
        {
            if (ConfigurationManager.Configuration.ImageEncoder == ImageEncoderType.NetVips)
            {
                var netVipsEncoderType = TryGetNetVipsEncoder();
                if (netVipsEncoderType is not null)
                {
                    return netVipsEncoderType;
                }

                Logger.LogWarning("libvips not available. Will fallback to {ImageEncoder}.", nameof(SkiaEncoder));
            }

            if (SkiaEncoder.IsNativeLibAvailable())
            {
                return typeof(SkiaEncoder);
            }

            Logger.LogWarning("Skia not available. Will fallback to {ImageEncoder}.", nameof(NullImageEncoder));
            return typeof(NullImageEncoder);
        }

        /// <summary>
        /// Resolves the NetVips encoder, or null when its native library is missing.
        /// </summary>
        /// <remarks>
        /// Deliberately kept in its own uninlined method: the JIT loads an assembly when it compiles
        /// the method that references it, not when the reference is reached, so folding this into
        /// <see cref="SelectImageEncoder"/> would start libvips on every server including the ones
        /// that stay on Skia.
        /// </remarks>
        /// <returns>The NetVips encoder type, or null.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Type? TryGetNetVipsEncoder()
        {
            if (!NetVipsEncoder.IsNativeLibAvailable())
            {
                return null;
            }

            Logger.LogInformation("Using the {ImageEncoder} image encoder.", nameof(NetVipsEncoder));

            return typeof(NetVipsEncoder);
        }

        /// <inheritdoc />
        protected override IEnumerable<Assembly> GetAssembliesWithPartsInternal()
        {
            // Jellyfin.Server
            yield return typeof(CoreAppHost).Assembly;

            // Jellyfin.Database.Implementations
            yield return typeof(JellyfinDbContext).Assembly;

            // Jellyfin.Server.Implementations
            yield return typeof(ServiceCollectionExtensions).Assembly;

            // Jellyfin.LiveTv
            yield return typeof(LiveTvManager).Assembly;
        }
    }
}
