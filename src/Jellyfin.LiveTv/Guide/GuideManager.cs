using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Guide;

/// <inheritdoc />
public class GuideManager : IGuideManager
{
    private const int MaxGuideDays = 14;
    private const string EtagKey = "ProgramEtag";
    private const string ExternalServiceTag = "ExternalServiceId";

    private static readonly ParallelOptions _cacheParallelOptions = new() { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 10) };
    private static readonly ParallelOptions _channelParallelOptions = new() { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) };

    private readonly ILogger<GuideManager> _logger;
    private readonly IConfigurationManager _config;
    private readonly IFileSystem _fileSystem;
    private readonly IItemRepository _itemRepo;
    private readonly ILibraryManager _libraryManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly LiveTvDtoService _tvDtoService;

    /// <summary>
    /// Amount of days images are pre-cached from external sources.
    /// </summary>
    public const int MaxCacheDays = 2;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{TCategoryName}"/>.</param>
    /// <param name="config">The <see cref="IConfigurationManager"/>.</param>
    /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
    /// <param name="itemRepo">The <see cref="IItemRepository"/>.</param>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="liveTvManager">The <see cref="ILiveTvManager"/>.</param>
    /// <param name="tunerHostManager">The <see cref="ITunerHostManager"/>.</param>
    /// <param name="recordingsManager">The <see cref="IRecordingsManager"/>.</param>
    /// <param name="tvDtoService">The <see cref="LiveTvDtoService"/>.</param>
    public GuideManager(
        ILogger<GuideManager> logger,
        IConfigurationManager config,
        IFileSystem fileSystem,
        IItemRepository itemRepo,
        ILibraryManager libraryManager,
        ILiveTvManager liveTvManager,
        ITunerHostManager tunerHostManager,
        IRecordingsManager recordingsManager,
        LiveTvDtoService tvDtoService)
    {
        _logger = logger;
        _config = config;
        _fileSystem = fileSystem;
        _itemRepo = itemRepo;
        _libraryManager = libraryManager;
        _liveTvManager = liveTvManager;
        _tunerHostManager = tunerHostManager;
        _recordingsManager = recordingsManager;
        _tvDtoService = tvDtoService;
    }

    /// <inheritdoc />
    public GuideInfo GetGuideInfo()
    {
        var startDate = DateTime.UtcNow;
        var endDate = startDate.AddDays(GetGuideDays());

        return new GuideInfo
        {
            StartDate = startDate,
            EndDate = endDate
        };
    }

    /// <inheritdoc />
    public async Task RefreshGuide(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting RefreshGuide operation");

        var recordingFoldersStart = System.Diagnostics.Stopwatch.StartNew();
        await _recordingsManager.CreateRecordingFolders().ConfigureAwait(false);
        recordingFoldersStart.Stop();
        _logger.LogDebug("Created recording folders in {ElapsedMs}ms", recordingFoldersStart.ElapsedMilliseconds);

        var tunerScanStart = System.Diagnostics.Stopwatch.StartNew();
        await _tunerHostManager.ScanForTunerDeviceChanges(cancellationToken).ConfigureAwait(false);
        tunerScanStart.Stop();
        _logger.LogDebug("Scanned for tuner device changes in {ElapsedMs}ms", tunerScanStart.ElapsedMilliseconds);

        var serviceCount = _liveTvManager.Services.Count;
        _logger.LogInformation("Found {ServiceCount} LiveTV services to process", serviceCount);

        var numComplete = 0;
        double progressPerService = serviceCount == 0
            ? 0
            : 1.0 / serviceCount;

        var newChannelIdList = new List<Guid>();
        var newProgramIdList = new List<Guid>();

        var cleanDatabase = true;

        foreach (var service in _liveTvManager.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Processing service {ServiceIndex}/{ServiceCount}: {ServiceName}", 
                numComplete + 1, serviceCount, service.Name);

            var serviceStart = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var innerProgress = new Progress<double>(p => progress.Report(p * progressPerService));

                var idList = await RefreshChannelsInternal(service, innerProgress, cancellationToken).ConfigureAwait(false);

                newChannelIdList.AddRange(idList.Item1);
                newProgramIdList.AddRange(idList.Item2);
                
                serviceStart.Stop();
                _logger.LogInformation("Completed service {ServiceName} in {ElapsedMs}ms. Added {ChannelCount} channels and {ProgramCount} programs", 
                    service.Name, serviceStart.ElapsedMilliseconds, idList.Item1.Count, idList.Item2.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                serviceStart.Stop();
                cleanDatabase = false;
                _logger.LogError(ex, "Error refreshing channels for service {ServiceName} after {ElapsedMs}ms", 
                    service.Name, serviceStart.ElapsedMilliseconds);
            }

            numComplete++;
            double percent = numComplete;
            percent /= serviceCount;

            progress.Report(100 * percent);
        }

        if (cleanDatabase)
        {
            _logger.LogInformation("Starting database cleanup for {ChannelCount} channels and {ProgramCount} programs", 
                newChannelIdList.Count, newProgramIdList.Count);
            
            var cleanupStart = System.Diagnostics.Stopwatch.StartNew();
            CleanDatabase(newChannelIdList.ToArray(), [BaseItemKind.LiveTvChannel], progress, cancellationToken);
            CleanDatabase(newProgramIdList.ToArray(), [BaseItemKind.LiveTvProgram], progress, cancellationToken);
            cleanupStart.Stop();
            
            _logger.LogInformation("Database cleanup completed in {ElapsedMs}ms", cleanupStart.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning("Skipping database cleanup due to previous errors");
        }

        var coreService = _liveTvManager.Services.OfType<DefaultLiveTvService>().FirstOrDefault();
        if (coreService is not null)
        {
            _logger.LogDebug("Refreshing timers for core service");
            var timerStart = System.Diagnostics.Stopwatch.StartNew();
            
            await coreService.RefreshSeriesTimers(cancellationToken).ConfigureAwait(false);
            await coreService.RefreshTimers(cancellationToken).ConfigureAwait(false);
            
            timerStart.Stop();
            _logger.LogDebug("Timer refresh completed in {ElapsedMs}ms", timerStart.ElapsedMilliseconds);
        }

        progress.Report(100);
        totalStopwatch.Stop();
        
        _logger.LogInformation("RefreshGuide completed in {TotalElapsedMs}ms. {ServiceCount} services, {TotalChannelCount} channels, {TotalProgramCount} programs", 
            totalStopwatch.ElapsedMilliseconds, serviceCount, newChannelIdList.Count, newProgramIdList.Count);
    }

    private double GetGuideDays()
    {
        var config = _config.GetLiveTvConfiguration();

        return config.GuideDays.HasValue
            ? Math.Clamp(config.GuideDays.Value, 1, MaxGuideDays)
            : 7;
    }

    private async Task<Tuple<List<Guid>, List<Guid>>> RefreshChannelsInternal(ILiveTvService service, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting RefreshChannelsInternal for service: {ServiceName}", service.Name);
        
        progress.Report(10);

        _logger.LogDebug("Getting channels from service: {ServiceName}", service.Name);
        var channelRetrievalStart = System.Diagnostics.Stopwatch.StartNew();
        var allChannelsList = (await service.GetChannelsAsync(cancellationToken).ConfigureAwait(false))
            .Select(i => new Tuple<string, ChannelInfo>(service.Name, i))
            .ToList();
        channelRetrievalStart.Stop();
        
        _logger.LogInformation("Retrieved {ChannelCount} channels from {ServiceName} in {ElapsedMs}ms", 
            allChannelsList.Count, service.Name, channelRetrievalStart.ElapsedMilliseconds);

        var list = new List<LiveTvChannel>();
        var parentFolder = _liveTvManager.GetInternalLiveTvFolder(cancellationToken);

        var numComplete = 0;

        // Use parallel processing only if we have channels to process
        if (allChannelsList.Count > 0)
        {
            _logger.LogInformation("Processing {ChannelCount} channels in parallel (max {MaxDegree})", 
                allChannelsList.Count, _channelParallelOptions.MaxDegreeOfParallelism);
            
            var channelProcessingStart = System.Diagnostics.Stopwatch.StartNew();
            // Process channels in parallel for better performance
            var channelLock = new object();

            await Parallel.ForEachAsync(
                allChannelsList,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _channelParallelOptions.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                async (channelInfo, ct) =>
                {
                    var channelStart = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        _logger.LogDebug("Processing channel: {ChannelName}", channelInfo.Item2.Name);
                        
                        var item = await GetChannel(channelInfo.Item2, channelInfo.Item1, parentFolder, ct).ConfigureAwait(false);
                        
                        channelStart.Stop();
                        _logger.LogDebug("Channel {ChannelName} processed in {ElapsedMs}ms", 
                            channelInfo.Item2.Name, channelStart.ElapsedMilliseconds);

                        lock (channelLock)
                        {
                            list.Add(item);
                            numComplete++;
                            double percent = (double)numComplete / allChannelsList.Count;
                            progress.Report((5 * percent) + 10);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        channelStart.Stop();
                        _logger.LogError(ex, "Error processing channel {Name} after {ElapsedMs}ms", 
                            channelInfo.Item2.Name, channelStart.ElapsedMilliseconds);

                        lock (channelLock)
                        {
                            numComplete++;
                            double percent = (double)numComplete / allChannelsList.Count;
                            progress.Report((5 * percent) + 10);
                        }
                    }
                }).ConfigureAwait(false);
            
            channelProcessingStart.Stop();
            _logger.LogInformation("Processed {ChannelCount} channels in {ElapsedMs}ms (avg {AvgMs}ms)", 
                allChannelsList.Count, channelProcessingStart.ElapsedMilliseconds, 
                channelProcessingStart.ElapsedMilliseconds / Math.Max(1, allChannelsList.Count));
        }
        else
        {
            _logger.LogInformation("No channels found for service {ServiceName}", service.Name);
        }

        progress.Report(15);

        var programIds = new List<Guid>();
        var channels = new List<Guid>();
        var guideDays = GetGuideDays();

        _logger.LogInformation("Starting program data processing for {ChannelCount} channels ({Days} days)", 
            list.Count, guideDays);

        var maxCacheDate = DateTime.UtcNow.AddDays(MaxCacheDays);

        // Process channels in parallel for program data
        var programLock = new object();
        var channelsLock = new object();
        numComplete = 0;

        // Use parallel processing only if we have channels to process
        if (list.Count > 0)
        {
            _logger.LogInformation("Processing program data for {ChannelCount} channels in parallel", list.Count);
            
            var programProcessingStart = System.Diagnostics.Stopwatch.StartNew();
            
            await Parallel.ForEachAsync(
                list,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _channelParallelOptions.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                async (currentChannel, ct) =>
                {
                    var channelProgramStart = System.Diagnostics.Stopwatch.StartNew();
                    _logger.LogDebug("Processing programs for channel: {ChannelName}", currentChannel.Name);
                    
                    lock (channelsLock)
                    {
                        channels.Add(currentChannel.Id);
                    }

                    try
                    {
                        var start = DateTime.UtcNow.AddHours(-1);
                        var end = start.AddDays(guideDays);

                        var isMovie = false;
                        var isSports = false;
                        var isNews = false;
                        var isKids = false;
                        var isSeries = false;

                        _logger.LogDebug("Fetching programs for {ChannelName} from {Start} to {End}", 
                            currentChannel.Name, start, end);
                        
                        var programRetrievalStart = System.Diagnostics.Stopwatch.StartNew();
                        var channelPrograms = (await service.GetProgramsAsync(currentChannel.ExternalId, start, end, ct).ConfigureAwait(false)).ToList();
                        programRetrievalStart.Stop();
                        
                        _logger.LogDebug("Retrieved {ProgramCount} programs for {ChannelName} in {ElapsedMs}ms", 
                            channelPrograms.Count, currentChannel.Name, programRetrievalStart.ElapsedMilliseconds);

                        var dbQueryStart = System.Diagnostics.Stopwatch.StartNew();
                        var existingPrograms = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                            ChannelIds = [currentChannel.Id],
                            DtoOptions = new DtoOptions(true)
                        }).Cast<LiveTvProgram>().ToDictionary(i => i.Id);
                        dbQueryStart.Stop();
                        
                        _logger.LogDebug("Queried {ExistingCount} existing programs for {ChannelName} in {ElapsedMs}ms", 
                            existingPrograms.Count, currentChannel.Name, dbQueryStart.ElapsedMilliseconds);

                        var newPrograms = new List<LiveTvProgram>();
                        var updatedPrograms = new List<LiveTvProgram>();

                        var programProcessingStart = System.Diagnostics.Stopwatch.StartNew();
                        foreach (var program in channelPrograms)
                        {
                            var (programItem, isNew, isUpdated) = GetProgram(program, existingPrograms, currentChannel);
                            if (isNew)
                            {
                                newPrograms.Add(programItem);
                            }
                            else if (isUpdated)
                            {
                                updatedPrograms.Add(programItem);
                            }

                            lock (programLock)
                            {
                                programIds.Add(programItem.Id);
                            }

                            isMovie |= program.IsMovie;
                            isSeries |= program.IsSeries;
                            isSports |= program.IsSports;
                            isNews |= program.IsNews;
                            isKids |= program.IsKids;
                        }
                        programProcessingStart.Stop();

                        _logger.LogDebug("Channel {Name}: {NewCount} new, {UpdatedCount} updated programs ({ElapsedMs}ms)",
                            currentChannel.Name, newPrograms.Count, updatedPrograms.Count, programProcessingStart.ElapsedMilliseconds);

                        if (newPrograms.Count > 0)
                        {
                            var createStart = System.Diagnostics.Stopwatch.StartNew();
                            _libraryManager.CreateItems(newPrograms, currentChannel, ct);
                            createStart.Stop();
                            
                            _logger.LogDebug("Created {NewProgramCount} programs for {ChannelName} in {ElapsedMs}ms", 
                                newPrograms.Count, currentChannel.Name, createStart.ElapsedMilliseconds);

                            var cacheStart = System.Diagnostics.Stopwatch.StartNew();
                            await PreCacheImages(newPrograms, maxCacheDate).ConfigureAwait(false);
                            cacheStart.Stop();
                            
                            _logger.LogDebug("Pre-cached {NewProgramCount} new program images for {ChannelName} in {ElapsedMs}ms", 
                                newPrograms.Count, currentChannel.Name, cacheStart.ElapsedMilliseconds);
                        }

                        if (updatedPrograms.Count > 0)
                        {
                            var updateStart = System.Diagnostics.Stopwatch.StartNew();
                            await _libraryManager.UpdateItemsAsync(
                                updatedPrograms,
                                currentChannel,
                                ItemUpdateType.MetadataImport,
                                ct).ConfigureAwait(false);
                            updateStart.Stop();
                            
                            _logger.LogDebug("Updated {UpdatedProgramCount} programs for {ChannelName} in {ElapsedMs}ms", 
                                updatedPrograms.Count, currentChannel.Name, updateStart.ElapsedMilliseconds);

                            var cacheStart = System.Diagnostics.Stopwatch.StartNew();
                            await PreCacheImages(updatedPrograms, maxCacheDate).ConfigureAwait(false);
                            cacheStart.Stop();
                            
                            _logger.LogDebug("Pre-cached {UpdatedProgramCount} updated program images for {ChannelName} in {ElapsedMs}ms", 
                                updatedPrograms.Count, currentChannel.Name, cacheStart.ElapsedMilliseconds);
                        }

                        currentChannel.IsMovie = isMovie;
                        currentChannel.IsNews = isNews;
                        currentChannel.IsSports = isSports;
                        currentChannel.IsSeries = isSeries;

                        if (isKids)
                        {
                            currentChannel.AddTag("Kids");
                        }

                        var metadataStart = System.Diagnostics.Stopwatch.StartNew();
                        await currentChannel.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
                        await currentChannel.RefreshMetadata(
                            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                            {
                                ForceSave = true
                            },
                            ct).ConfigureAwait(false);
                        metadataStart.Stop();
                        
                        channelProgramStart.Stop();
                        _logger.LogDebug("Completed {ChannelName} processing in {TotalElapsedMs}ms (metadata: {MetadataMs}ms)", 
                            currentChannel.Name, channelProgramStart.ElapsedMilliseconds, metadataStart.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        channelProgramStart.Stop();
                        _logger.LogError(ex, "Error processing programs for {Name} after {ElapsedMs}ms", 
                            currentChannel.Name, channelProgramStart.ElapsedMilliseconds);
                    }

                    lock (channelsLock)
                    {
                        numComplete++;
                        double percent = (double)numComplete / list.Count;
                        progress.Report((85 * percent) + 15);
                    }
                }).ConfigureAwait(false);
            
            programProcessingStart.Stop();
            _logger.LogInformation("Completed program processing for {ChannelCount} channels in {ElapsedMs}ms (avg {AvgMs}ms)", 
                list.Count, programProcessingStart.ElapsedMilliseconds, programProcessingStart.ElapsedMilliseconds / Math.Max(1, list.Count));
        }
        else
        {
            _logger.LogInformation("No channels to process for program data");
        }

        progress.Report(100);
        stopwatch.Stop();
        _logger.LogInformation("RefreshChannelsInternal for {ServiceName} completed in {TotalElapsedMs}ms. {ChannelCount} channels, {ChannelIdCount} channel IDs, {ProgramIdCount} program IDs", 
            service.Name, stopwatch.ElapsedMilliseconds, allChannelsList.Count, channels.Count, programIds.Count);
        
        return new Tuple<List<Guid>, List<Guid>>(channels, programIds);
    }

    private void CleanDatabase(Guid[] currentIdList, BaseItemKind[] validTypes, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var list = _itemRepo.GetItemIdsList(new InternalItemsQuery
        {
            IncludeItemTypes = validTypes,
            DtoOptions = new DtoOptions(false)
        });

        var numComplete = 0;

        foreach (var itemId in list)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (itemId.IsEmpty())
            {
                // Somehow some invalid data got into the db. It probably predates the boundary checking
                continue;
            }

            if (!currentIdList.Contains(itemId))
            {
                var item = _libraryManager.GetItemById(itemId);

                if (item is not null)
                {
                    _libraryManager.DeleteItem(
                        item,
                        new DeleteOptions
                        {
                            DeleteFileLocation = false,
                            DeleteFromExternalProvider = false
                        },
                        false);
                }
            }

            numComplete++;
            double percent = numComplete / (double)list.Count;

            progress.Report(100 * percent);
        }
    }

    private async Task<LiveTvChannel> GetChannel(
        ChannelInfo channelInfo,
        string serviceName,
        BaseItem parentFolder,
        CancellationToken cancellationToken)
    {
        var parentFolderId = parentFolder.Id;
        var isNew = false;
        var forceUpdate = false;

        var id = _tvDtoService.GetInternalChannelId(serviceName, channelInfo.Id);

        if (_libraryManager.GetItemById(id) is not LiveTvChannel item)
        {
            item = new LiveTvChannel
            {
                Name = channelInfo.Name,
                Id = id,
                DateCreated = DateTime.UtcNow
            };

            isNew = true;
        }

        if (channelInfo.Tags is not null)
        {
            if (!channelInfo.Tags.SequenceEqual(item.Tags, StringComparer.OrdinalIgnoreCase))
            {
                isNew = true;
            }

            item.Tags = channelInfo.Tags;
        }

        if (!item.ParentId.Equals(parentFolderId))
        {
            isNew = true;
        }

        item.ParentId = parentFolderId;

        item.ChannelType = channelInfo.ChannelType;
        item.ServiceName = serviceName;

        if (!string.Equals(item.GetProviderId(ExternalServiceTag), serviceName, StringComparison.OrdinalIgnoreCase))
        {
            forceUpdate = true;
        }

        item.SetProviderId(ExternalServiceTag, serviceName);

        if (!string.Equals(channelInfo.Id, item.ExternalId, StringComparison.Ordinal))
        {
            forceUpdate = true;
        }

        item.ExternalId = channelInfo.Id;

        if (!string.Equals(channelInfo.Number, item.Number, StringComparison.Ordinal))
        {
            forceUpdate = true;
        }

        item.Number = channelInfo.Number;

        if (!string.Equals(channelInfo.Name, item.Name, StringComparison.Ordinal))
        {
            forceUpdate = true;
        }

        item.Name = channelInfo.Name;

        if (!item.HasImage(ImageType.Primary))
        {
            if (!string.IsNullOrWhiteSpace(channelInfo.ImagePath))
            {
                item.SetImagePath(ImageType.Primary, channelInfo.ImagePath);
                forceUpdate = true;
            }
            else if (!string.IsNullOrWhiteSpace(channelInfo.ImageUrl))
            {
                item.SetImagePath(ImageType.Primary, channelInfo.ImageUrl);
                forceUpdate = true;
            }
        }

        if (isNew)
        {
            _libraryManager.CreateItem(item, parentFolder);
        }
        else if (forceUpdate)
        {
            await _libraryManager.UpdateItemAsync(item, parentFolder, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        }

        return item;
    }

    private (LiveTvProgram Item, bool IsNew, bool IsUpdated) GetProgram(
        ProgramInfo info,
        Dictionary<Guid, LiveTvProgram> allExistingPrograms,
        LiveTvChannel channel)
    {
        var id = _tvDtoService.GetInternalProgramId(info.Id);

        var isNew = false;
        var forceUpdate = false;

        if (!allExistingPrograms.TryGetValue(id, out var item))
        {
            isNew = true;
            item = new LiveTvProgram
            {
                Name = info.Name,
                Id = id,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow
            };

            item.TrySetProviderId(EtagKey, info.Etag);
        }

        if (!string.Equals(info.ShowId, item.ShowId, StringComparison.OrdinalIgnoreCase))
        {
            item.ShowId = info.ShowId;
            forceUpdate = true;
        }

        var channelId = channel.Id;
        if (!item.ParentId.Equals(channelId))
        {
            item.ParentId = channel.Id;
            forceUpdate = true;
        }

        item.Audio = info.Audio;
        item.ChannelId = channelId;
        item.CommunityRating = info.CommunityRating;
        item.EpisodeTitle = info.EpisodeTitle;
        item.ExternalId = info.Id;

        var seriesId = info.SeriesId;
        if (!string.IsNullOrWhiteSpace(seriesId) && !string.Equals(item.ExternalSeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
        {
            item.ExternalSeriesId = seriesId;
            forceUpdate = true;
        }

        var isSeries = info.IsSeries || !string.IsNullOrEmpty(info.EpisodeTitle);
        if (isSeries || !string.IsNullOrEmpty(info.EpisodeTitle))
        {
            item.SeriesName = info.Name;
        }

        var tags = new List<string>();
        if (info.IsLive)
        {
            tags.Add("Live");
        }

        if (info.IsPremiere)
        {
            tags.Add("Premiere");
        }

        if (info.IsNews)
        {
            tags.Add("News");
        }

        if (info.IsSports)
        {
            tags.Add("Sports");
        }

        if (info.IsKids)
        {
            tags.Add("Kids");
        }

        if (info.IsRepeat)
        {
            tags.Add("Repeat");
        }

        if (info.IsMovie)
        {
            tags.Add("Movie");
        }

        if (isSeries)
        {
            tags.Add("Series");
        }

        item.Tags = tags.ToArray();
        item.Genres = info.Genres.ToArray();

        if (info.IsHD ?? false)
        {
            item.Width = 1280;
            item.Height = 720;
        }

        item.IsMovie = info.IsMovie;
        item.IsRepeat = info.IsRepeat;
        if (item.IsSeries != isSeries)
        {
            item.IsSeries = isSeries;
            forceUpdate = true;
        }

        item.Name = info.Name;
        item.OfficialRating = info.OfficialRating;
        item.Overview = info.Overview;
        item.RunTimeTicks = (info.EndDate - info.StartDate).Ticks;
        foreach (var providerId in info.SeriesProviderIds)
        {
            info.ProviderIds["Series" + providerId.Key] = providerId.Value;
        }

        item.ProviderIds = info.ProviderIds;
        if (item.StartDate != info.StartDate)
        {
            item.StartDate = info.StartDate;
            forceUpdate = true;
        }

        if (item.EndDate != info.EndDate)
        {
            item.EndDate = info.EndDate;
            forceUpdate = true;
        }

        item.ProductionYear = info.ProductionYear;
        if (!isSeries || info.IsRepeat)
        {
            item.PremiereDate = info.OriginalAirDate;
        }

        item.IndexNumber = info.EpisodeNumber;
        item.ParentIndexNumber = info.SeasonNumber;

        forceUpdate |= UpdateImages(item, info);

        if (isNew)
        {
            item.OnMetadataChanged();

            return (item, true, false);
        }

        var isUpdated = forceUpdate;
        var etag = info.Etag;
        if (string.IsNullOrWhiteSpace(etag))
        {
            isUpdated = true;
        }
        else if (!string.Equals(etag, item.GetProviderId(EtagKey), StringComparison.OrdinalIgnoreCase))
        {
            item.SetProviderId(EtagKey, etag);
            isUpdated = true;
        }

        if (isUpdated)
        {
            item.OnMetadataChanged();

            return (item, false, true);
        }

        return (item, false, false);
    }

    private static bool UpdateImages(BaseItem item, ProgramInfo info)
    {
        var updated = false;

        // Primary
        updated |= UpdateImage(ImageType.Primary, item, info);

        // Thumbnail
        updated |= UpdateImage(ImageType.Thumb, item, info);

        // Logo
        updated |= UpdateImage(ImageType.Logo, item, info);

        // Backdrop
        updated |= UpdateImage(ImageType.Backdrop, item, info);

        return updated;
    }

    private static bool UpdateImage(ImageType imageType, BaseItem item, ProgramInfo info)
    {
        var image = item.GetImages(imageType).FirstOrDefault();
        var currentImagePath = image?.Path;
        var newImagePath = imageType switch
        {
            ImageType.Primary => info.ImagePath,
            _ => null
        };
        var newImageUrl = imageType switch
        {
            ImageType.Backdrop => info.BackdropImageUrl,
            ImageType.Logo => info.LogoImageUrl,
            ImageType.Primary => info.ImageUrl,
            ImageType.Thumb => info.ThumbImageUrl,
            _ => null
        };

        var sameImage = (currentImagePath?.Equals(newImageUrl, StringComparison.OrdinalIgnoreCase) ?? false)
                                || (currentImagePath?.Equals(newImagePath, StringComparison.OrdinalIgnoreCase) ?? false);
        if (sameImage)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(newImagePath))
        {
            item.SetImage(
                new ItemImageInfo
                {
                    Path = newImagePath,
                    Type = imageType
                },
                0);

            return true;
        }

        if (!string.IsNullOrWhiteSpace(newImageUrl))
        {
            item.SetImage(
                new ItemImageInfo
                {
                    Path = newImageUrl,
                    Type = imageType
                },
                0);

            return true;
        }

        item.RemoveImage(image);

        return false;
    }

    private async Task PreCacheImages(IReadOnlyList<BaseItem> programs, DateTime maxCacheDate)
    {
        await Parallel.ForEachAsync(
            programs
                .Where(p => p.EndDate.HasValue && p.EndDate.Value < maxCacheDate)
                .DistinctBy(p => p.Id),
            _cacheParallelOptions,
            async (program, cancellationToken) =>
            {
                for (var i = 0; i < program.ImageInfos.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var imageInfo = program.ImageInfos[i];
                    if (!imageInfo.IsLocalFile)
                    {
                        _logger.LogDebug("Caching image locally: {Url}", imageInfo.Path);
                        try
                        {
                            program.ImageInfos[i] = await _libraryManager.ConvertImageToLocal(
                                    program,
                                    imageInfo,
                                    imageIndex: 0,
                                    removeOnFailure: false)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Unable to pre-cache {Url}", imageInfo.Path);
                        }
                    }
                }
            }).ConfigureAwait(false);
    }
}
