using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using HtmlAgilityPack;
using Serilog;
// ReSharper disable VirtualMemberCallInConstructor

namespace EbayKleinanzeigenCrawler.Parser;

public abstract class ParserBase : IParser
{
    protected readonly ILogger Logger;
    private readonly IQueryExecutor _queryExecutor;
    
    protected ParserBase(ILogger logger, IQueryExecutor queryExecutor)
    {
        Logger = logger;
        _queryExecutor = queryExecutor;
        _queryExecutor.Initialize(
            timeToWaitBetweenMaxAmountOfRequests: TimeToWaitBetweenMaxAmountOfRequests, 
            allowedRequestsPerTimespan: AllowedRequestsPerTimespan,
            invalidHtml: InvalidHtml
        );
    }

    protected abstract TimeSpan TimeToWaitBetweenMaxAmountOfRequests { get; }
    protected abstract uint AllowedRequestsPerTimespan { get; }

    protected abstract string InvalidHtml { get; }

    protected abstract bool EnsureValidHtml(HtmlDocument resultPage);

    public abstract List<Uri> GetAdditionalPages(HtmlDocument document);

    protected abstract List<HtmlNode> ParseResults(HtmlDocument resultPage);

    protected abstract bool ShouldSkipResult(HtmlNode result);

    protected abstract Uri ParseResultLink(HtmlNode result);

    protected abstract string ParseResultDate(HtmlNode result);

    protected abstract string ParseResultPrice(HtmlNode result);

    protected abstract string ParseTitle(HtmlDocument document);

    protected abstract string ParseDescriptionText(HtmlDocument document);

    public IQueryExecutor GetQueryExecutor()
    {
        return _queryExecutor;
    }

    public IEnumerable<Result> ParseLinks(HtmlDocument resultPage)
    {
        if (!EnsureValidHtml(resultPage))
        {
            Logger.Warning("Skipping parsing link due to invalid HTML");
            yield break;
        }
        
        var results = ParseResults(resultPage);

        if (results is null)
        {
            // When no results are found
            yield break;
        }

        foreach (var result in results)
        {
            if (ShouldSkipResult(result))
            {
                continue;
            }

            // Validation must happen in the implementations
            var link = ParseResultLink(result);
            var date = ParseResultDate(result);
            var price = ParseResultPrice(result);
            yield return new Result { Link = link, CreationDate = date ?? "", Price = price ?? "" };
        }
    }

    public virtual bool IsMatch(HtmlDocument document, Subscription subscription)
    {
        if (document.DocumentNode.InnerHtml.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return false;
        }

        if (subscription.IncludeKeywords is null)
        {
            throw new InvalidOperationException("IncludeKeywords cannot be null");
        }

        if (subscription.ExcludeKeywords is null)
        {
            throw new InvalidOperationException("ExcludeKeywords cannot be null");
        }

        var title = ParseTitle(document);
        if (string.IsNullOrWhiteSpace(title))
        {
            Logger.Error(document.DocumentNode.InnerHtml);
            throw new InvalidOperationException("Could not parse title");
        }

        var descriptionText = ParseDescriptionText(document);
        if (string.IsNullOrWhiteSpace(descriptionText))
        {
            Logger.Error(document.DocumentNode.InnerHtml);
            throw new InvalidOperationException("Could not parse description");
        }

        var allIncludeKeywordsFound = HtmlContainsAllIncludeKeywords(subscription, title + descriptionText);
        var excludeKeywordsFound = HtmlContainsAnyExcludeKeywords(subscription, title + descriptionText);
        return allIncludeKeywordsFound && !excludeKeywordsFound;
    }

    private bool HtmlContainsAllIncludeKeywords(Subscription subscription, string descriptionText)
    {
        if (subscription.IncludeKeywords.Count == 0)
        {
            return true;
        }

        // For a keyword "foo | bar", only one of the disjunct keywords must be included
        var disjunctionGroups = subscription.IncludeKeywords
            .Where(str => str.Contains("|"))
            .Select(str => str
                .Split("|")
                .Select(keyword => keyword.Trim())
                .ToList()
            );

        foreach (var group in disjunctionGroups)
        {
            var keywordsOfGroupInText = group.Where(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
            if (!keywordsOfGroupInText.Any())
            {
                Logger.Verbose($"Not a match because no keyword found from '{string.Join(" | ", group)}'");
                return false;
            }
        }

        var allNonDisjunctiveKeywordsFound = subscription.IncludeKeywords
            .Where(k => !k.Contains("|"))
            .All(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));

        return allNonDisjunctiveKeywordsFound;
    }

    private bool HtmlContainsAnyExcludeKeywords(Subscription subscription, string descriptionText)
    {
        if (subscription.ExcludeKeywords.Count == 0)
        {
            return false;
        }

        return subscription.ExcludeKeywords.Any(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
    }
}