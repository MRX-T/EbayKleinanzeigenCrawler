using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Persistence;

internal class AlreadyProcessedUrlsPersistence : IAlreadyProcessedUrlsPersistence
{
    private readonly object _lockObject = new();
    private readonly ILogger _logger;
    private readonly IDataStorage _dataStorage;
    private ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>> _alreadyProcessedUrlsPerSubscription;
    private readonly string _filePath = Path.Join("data", "AlreadyProcessedUrls.json");

    public AlreadyProcessedUrlsPersistence(ILogger logger, IDataStorage dataStorage)
    {
        Directory.CreateDirectory("data");
        _logger = logger;
        _dataStorage = dataStorage;
    }

    public List<AlreadyProcessedUrl> GetAlreadyProcessedLinksForSubscription(Guid subscriptionId)
    {
        lock(_lockObject)
        {
            return _alreadyProcessedUrlsPerSubscription.GetOrAdd(subscriptionId, valueFactory: _ => new List<AlreadyProcessedUrl>());
        }
    }

    public void RestoreData()
    {
        lock (_lockObject)
        {
            try
            {
                _dataStorage.Load(_filePath, out ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>> data); // TODO: remove URLs for deleted Subscriptions
                _alreadyProcessedUrlsPerSubscription = data;
                _logger.Information($"Restored processed URLs for {_alreadyProcessedUrlsPerSubscription.Count} subscriptions");
            }
            catch (FileNotFoundException e)
            {
                _logger.Warning($"File '{_filePath}' not found ('{e.Message}'). Starting clean.");
                _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
            }
            catch (JsonSerializationException e) when (e.Message.StartsWith("Error converting value"))
            {
                _logger.Warning($"Value conversion error when loading '{_filePath}'. Assuming this is because the file has the old format. Attempting conversion.");
                TryConvertOldFormatFile();
            }
            catch (Exception e)
            {
                _logger.Error(e, $"Error when restoring subscribers: '{e.Message}'. Starting clean.");
                _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
            }
        }

        // Make sure the in-memory storage and the file are in sync
        SaveData();
    }

    private void TryConvertOldFormatFile()
    {
        try
        {
            var backup = _filePath + ".bak";
            if (!File.Exists(backup))
            {
                File.Copy(_filePath, backup);
            }
            _dataStorage.Load(_filePath, out ConcurrentDictionary<Guid, List<Uri>> oldData);
            _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
            foreach (var (subscriptionId, uris) in oldData)
            {
                var newUris = uris
                    .Select(uri => new AlreadyProcessedUrl
                    {
                        Uri = uri,
                        LastFound = DateTime.Now
                    })
                    .ToList();

                _alreadyProcessedUrlsPerSubscription.TryAdd(subscriptionId, newUris);
            }

            SaveData();
            _logger.Warning($"Conversion finished. Restored processed URLs for {_alreadyProcessedUrlsPerSubscription.Count} subscriptions");
        }
        catch (Exception e1)
        {
            _logger.Error(e1, "Conversion failed! Assuming broken file. Starting clean");
            _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
        }
    }

    public void SaveData()
    {
        lock(_lockObject)
        {
            // Remove urls which have not been found for more than X days as it's safe to assume they won't be found again
            // Hint: If the app was turned off longer than that, there might be some duplicate notifications about matches.
            foreach (var element in _alreadyProcessedUrlsPerSubscription)
            {
                var urls = element.Value;
                foreach (var url in urls)
                {
                    if (url.LastFound < DateTime.Now - TimeSpan.FromDays(31))
                    {
                        urls.Remove(url);
                    }
                }
            }

            _dataStorage.Save(_alreadyProcessedUrlsPerSubscription, _filePath);
        }
    }
}