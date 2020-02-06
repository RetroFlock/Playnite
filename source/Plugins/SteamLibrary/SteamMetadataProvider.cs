﻿using Playnite.SDK;
using Playnite.SDK.Metadata;
using Playnite.SDK.Models;
using SteamLibrary.Models;
using SteamLibrary.Services;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.Common.Web;
using System.Diagnostics;

namespace SteamLibrary
{
    public class SteamMetadataProvider : LibraryMetadataProvider
    {
        private ILogger logger = LogManager.GetLogger();
        private SteamServicesClient playniteServices;
        private SteamLibrary library;
        private SteamApiClient apiClient;

        private readonly string[] backgroundUrls = new string[]
        {
            @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page.bg.jpg",
            @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page_bg_generated.jpg"
        };

        public SteamMetadataProvider(SteamServicesClient playniteServices, SteamLibrary library, SteamApiClient apiClient)
        {
            this.library = library;
            this.playniteServices = playniteServices;
            this.apiClient = apiClient;
        }

        #region IMetadataProvider

        public override GameMetadata GetMetadata(Game game)
        {
            var gameData = new Game("SteamGame")
            {
                GameId = game.GameId
            };

            var gameId = game.ToSteamGameID();
            if (gameId.IsMod)
            {
                var data = library.GetInstalledModFromFolder(game.InstallDirectory, ModInfo.GetModTypeOfGameID(gameId));
                return new GameMetadata(data, null, null, null);
            }
            else
            {
                return GetGameMetadata(gameId);
            }
        }

        #endregion IMetadataProvider

        internal KeyValue GetAppInfo(uint appId)
        {
            try
            {
                return apiClient.GetProductInfo(appId).GetAwaiter().GetResult();
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, $"Failed to get Steam appinfo {appId}");
                return null;
            }
        }

        internal StoreAppDetailsResult.AppDetails GetStoreData(uint appId)
        {
            var stringData = string.Empty;

            // First try to get cached data
            try
            {
                stringData = playniteServices.GetSteamStoreData(appId);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to get Steam store cache data.");
            }

            // If no cache then download on client and push to cache
            if (string.IsNullOrEmpty(stringData))
            {
                // Steam may return 429 if we put too many request
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        stringData = WebApiClient.GetRawStoreAppDetail(appId);
                        logger.Debug($"Steam store data got from live server {appId}");

                        try
                        {
                            playniteServices.PostSteamStoreData(appId, stringData);
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, $"Failed to post steam store data to cache {appId}");
                        }

                        break;
                    }
                    catch (WebException e)
                    {
                        if (i + 1 == 10)
                        {
                            logger.Error($"Reached download timeout for Steam store game {appId}");
                            return null;
                        }

                        if (e.Message.Contains("429"))
                        {
                            Thread.Sleep(2500);
                            continue;
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
            else
            {
                logger.Debug($"Steam store data got from cache {appId}");
            }

            if (!string.IsNullOrEmpty(stringData))
            {
                var response = WebApiClient.ParseStoreData(appId, stringData);
                if (response.success != true)
                {
                    return null;
                }

                return response.data;
            }

            return null;
        }

        internal SteamGameMetadata DownloadGameMetadata(uint appId, SteamLibrarySettings settings)
        {
            var metadata = new SteamGameMetadata();
            var productInfo = GetAppInfo(appId);
            metadata.ProductDetails = productInfo;

            try
            {
                metadata.StoreDetails = GetStoreData(appId);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to download Steam store metadata {appId}");
            }

            // Icon
            if (productInfo != null)
            {
                var iconRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.ico";
                var icon = productInfo["common"]["clienticon"];
                var iconUrl = string.Empty;
                if (!string.IsNullOrEmpty(icon.Value))
                {
                    iconUrl = string.Format(iconRoot, appId, icon.Value);
                }
                else
                {
                    var newIcon = productInfo["common"]["icon"];
                    if (!string.IsNullOrEmpty(newIcon.Value))
                    {
                        iconRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.jpg";
                        iconUrl = string.Format(iconRoot, appId, newIcon.Value);
                    }
                }

                // There might be no icon assigned to game
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    metadata.Icon = new MetadataFile(iconUrl);
                }
            }

            // Image
            var useBanner = false;
            if (settings.DownloadVerticalCovers)
            {
                var imageRoot = @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900_2x.jpg";
                var imageUrl = string.Format(imageRoot, appId);
                if (HttpDownloader.GetResponseCode(imageUrl) == HttpStatusCode.OK)
                {
                    metadata.CoverImage = new MetadataFile(imageUrl);
                }
                else
                {
                    useBanner = true;
                }
            }

            if (useBanner || !settings.DownloadVerticalCovers)
            {
                var imageRoot = @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/header.jpg";
                var imageUrl = string.Format(imageRoot, appId);
                if (HttpDownloader.GetResponseCode(imageUrl) == HttpStatusCode.OK)
                {
                    metadata.CoverImage = new MetadataFile(imageUrl);
                }
            }

            if (metadata.CoverImage == null)
            {
                if (productInfo != null)
                {
                    var imageRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.jpg";
                    var image = productInfo["common"]["logo"];
                    if (!string.IsNullOrEmpty(image.Value))
                    {
                        var imageUrl = string.Format(imageRoot, appId, image.Value);
                        metadata.CoverImage = new MetadataFile(imageUrl);
                    }
                }
            }

            // Background Image
            switch (settings.BackgroundSource)
            {
                case BackgroundSource.Image:
                    metadata.BackgroundImage = new MetadataFile(GetGameBackground(appId));
                    break;
                case BackgroundSource.StoreScreenshot:
                    if (metadata.StoreDetails != null)
                    {
                        metadata.BackgroundImage = new MetadataFile(Regex.Replace(metadata.StoreDetails.screenshots.First().path_full, "\\?.*$", ""));
                    }
                    break;
                case BackgroundSource.StoreBackground:
                    metadata.BackgroundImage = new MetadataFile(string.Format(@"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page_bg_generated_v6b.jpg", appId));
                    break;
                case BackgroundSource.Banner:
                    metadata.BackgroundImage = new MetadataFile(string.Format(@"https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_hero.jpg", appId));
                    break;
                default:
                    break;
            }

            return metadata;
        }

        internal string ParseDescription(string description)
        {
            return description.Replace("%CDN_HOST_MEDIA_SSL%", "steamcdn-a.akamaihd.net");
        }

        internal GameMetadata GetGameMetadata(GameID gameId)
        {
            var resources = library.PlayniteApi.Resources;
            var appId = gameId.AppID;
            var downloadedMetadata = DownloadGameMetadata(appId, library.LibrarySettings);
            var gameInfo = new GameInfo
            {
                Name = downloadedMetadata.ProductDetails?["common"]["name"]?.Value ?? downloadedMetadata.GameInfo.Name,
                Links = new List<Link>()
                {
                    new Link(resources.GetString("LOCSteamLinksCommunityHub"), $"https://steamcommunity.com/app/{appId}"),
                    new Link(resources.GetString("LOCSteamLinksDiscussions"), $"https://steamcommunity.com/app/{appId}/discussions/"),
                    new Link(resources.GetString("LOCCommonLinksNews"), $"https://store.steampowered.com/news/?appids={appId}"),
                    new Link(resources.GetString("LOCCommonLinksStorePage"), $"https://store.steampowered.com/app/{appId}"),
                    new Link("PCGamingWiki", $"https://pcgamingwiki.com/api/appid.php?appid={appId}")
                }
            };

            downloadedMetadata.GameInfo = gameInfo;

            var metadata = new GameMetadata()
            {
                GameInfo = gameInfo,
                Icon = downloadedMetadata.Icon,
                CoverImage = downloadedMetadata.CoverImage,
                BackgroundImage = downloadedMetadata.BackgroundImage
            };

            if (downloadedMetadata.StoreDetails?.categories?.FirstOrDefault(a => a.id == 22) != null)
            {
                gameInfo.Links.Add(new Link(resources.GetString("LOCCommonLinksAchievements"), Steam.GetAchievementsUrl(appId)));
            }

            if (downloadedMetadata.StoreDetails?.categories?.FirstOrDefault(a => a.id == 30) != null)
            {
                gameInfo.Links.Add(new Link(resources.GetString("LOCSteamLinksWorkshop"), Steam.GetWorkshopUrl(appId)));
            }

            if (downloadedMetadata.StoreDetails != null)
            {
                gameInfo.Description = ParseDescription(downloadedMetadata.StoreDetails.detailed_description);
                var cultInfo = new CultureInfo("en-US", false).TextInfo;
                gameInfo.ReleaseDate = downloadedMetadata.StoreDetails.release_date.date;
                gameInfo.CriticScore = downloadedMetadata.StoreDetails.metacritic?.score;

                if (downloadedMetadata.StoreDetails.publishers.HasNonEmptyItems())
                {
                    gameInfo.Publishers = new List<string>(downloadedMetadata.StoreDetails.publishers);
                }

                if (downloadedMetadata.StoreDetails.developers.HasNonEmptyItems())
                {
                    gameInfo.Developers = new List<string>(downloadedMetadata.StoreDetails.developers);
                }

                if (downloadedMetadata.StoreDetails.categories.HasItems())
                {
                    gameInfo.Features = new List<string>();
                    foreach (var category in downloadedMetadata.StoreDetails.categories)
                    {
                        // Ignore VR category, will be set from appinfo
                        if (category.id == 31)
                        {
                            continue;
                        }

                        gameInfo.Features.Add(cultInfo.ToTitleCase(category.description));
                    }
                }

                if (downloadedMetadata.StoreDetails.genres.HasItems())
                {
                    gameInfo.Genres = new List<string>(downloadedMetadata.StoreDetails.genres.Select(a => a.description));
                }
            }

            if (downloadedMetadata.ProductDetails != null)
            {
                var tasks = new List<GameAction>();
                var launchList = downloadedMetadata.ProductDetails["config"]["launch"].Children;
                foreach (var task in launchList.Skip(1))
                {
                    var properties = task["config"];
                    if (properties.Name != null)
                    {
                        if (properties["oslist"].Name != null)
                        {
                            if (properties["oslist"].Value != "windows")
                            {
                                continue;
                            }
                        }
                    }

                    // Ignore action without name  - shoudn't be visible to end user
                    if (task["description"].Name != null)
                    {
                        var newTask = new GameAction()
                        {
                            Name = task["description"].Value,
                            Arguments = task["arguments"].Value ?? string.Empty,
                            Path = task["executable"].Value,
                            IsHandledByPlugin = false,
                            WorkingDir = ExpandableVariables.InstallationDirectory
                        };

                        tasks.Add(newTask);
                    }
                }

                var manual = downloadedMetadata.ProductDetails["extended"]["gamemanualurl"];
                if (manual.Name != null)
                {
                    tasks.Add((new GameAction()
                    {
                        Name = "Manual",
                        Type = GameActionType.URL,
                        Path = manual.Value,
                        IsHandledByPlugin = false
                    }));
                }

                gameInfo.OtherActions = tasks;

                // VR features
                var vrSupport = false;
                foreach (var vrArea in downloadedMetadata.ProductDetails["common"]["playareavr"].Children)
                {
                    if (vrArea.Name == "seated" && vrArea.Value == "1")
                    {
                        gameInfo.Features.Add("VR Seated");
                        vrSupport = true;
                    }
                    else if (vrArea.Name == "standing" && vrArea.Value == "1")
                    {
                        gameInfo.Features.Add("VR Standing");
                        vrSupport = true;
                    }
                    if (vrArea.Name.Contains("roomscale"))
                    {
                        gameInfo.Features.AddMissing("VR Room-Scale");
                        vrSupport = true;
                    }
                }

                foreach (var vrArea in downloadedMetadata.ProductDetails["common"]["controllervr"].Children)
                {
                    if (vrArea.Name == "kbm" && vrArea.Value == "1")
                    {
                        gameInfo.Features.Add("VR Keyboard / Mouse");
                        vrSupport = true;
                    }
                    else if (vrArea.Name == "xinput" && vrArea.Value == "1")
                    {
                        gameInfo.Features.Add("VR Gamepad");
                        vrSupport = true;
                    }
                    if ((vrArea.Name == "oculus" && vrArea.Value == "1") ||
                        (vrArea.Name == "steamvr" && vrArea.Value == "1"))
                    {
                        gameInfo.Features.Add("VR Motion Controllers");
                        vrSupport = true;
                    }
                }

                if (vrSupport)
                {
                    gameInfo.Features.Add("VR");
                }
            }

            return downloadedMetadata;
        }

        private string GetGameBackground(uint appId)
        {
            foreach (var url in backgroundUrls)
            {
                var bk = string.Format(url, appId);
                if (HttpDownloader.GetResponseCode(bk) == HttpStatusCode.OK)
                {
                    return bk;
                }
            }

            return null;
        }
    }
}