﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Web.Mvc;
using StackExchange.DataExplorer.Helpers;
using StackExchange.DataExplorer.Models;

namespace StackExchange.DataExplorer.Controllers
{
    public class QueryController : StackOverflowController
    {
        [HttpPost]
        [Route(@"query/save/{parentId?:\d+}")]
        public ActionResult Create(string sql, string title, string description, int siteId, int? parentId, bool? textResults, bool? executionPlan, bool? crossSite, bool? excludeMetas)
        {
            if (CurrentUser.IsAnonymous && !CaptchaController.CaptchaPassed(GetRemoteIP()))
            {
                return Json(new { captcha = true });
            }

            ActionResult response = null;

            try
            {
                Revision parent = null;

                if (parentId.HasValue)
                {
                    parent = GetBasicRevision(parentId.Value);

                    if (parent == null)
                    {
                        throw new ApplicationException("Invalid revision ID");
                    }
                }

                var parsedQuery = new ParsedQuery(
                    sql,
                    Request.Params,
                    executionPlan == true,
                    crossSite == true,
                    excludeMetas == true
                );
                var results = ExecuteWithResults(parsedQuery, siteId, textResults == true);
                var query = Current.DB.Query<Query>(
                    "SELECT * FROM Queries WHERE QueryHash = @hash",
                    new
                    {
                        hash = parsedQuery.Hash
                    }
                ).FirstOrDefault();

                int saveId = 0, queryId;
                DateTime saveTime;

                // We only create revisions if something actually changed.
                // We'll log it as an execution anyway if applicable, so the user will
                // still get a link in their profile, just not their own revision.
                if (!(parent != null && query != null && query.Id == parent.Id))
                {
                    if (query == null)
                    {
                        queryId = (int)Current.DB.Query<decimal>(@"
                            INSERT INTO Queries(
                                QueryHash, QueryBody
                            ) VALUES(
                                @hash, @body
                            )

                            SELECT SCOPE_IDENTITY()",
                            new
                            {
                                hash = parsedQuery.Hash,
                                body = parsedQuery.RawSql
                            }
                        ).First();
                    }
                    else
                    {
                        queryId = query.Id;
                    }

                    saveId = (int)Current.DB.Query<decimal>(@"
                        INSERT INTO Revisions(
                            QueryId, RootId, OwnerId, OwnerIP, CreationDate
                        ) VALUES(
                            @query, @root, @owner, @ip, @creation
                        )

                        SELECT SCOPE_IDENTITY()",
                        new
                        {
                            query = queryId,
                            root = parent != null ? (int?)parent.Id : null,
                            owner = CurrentUser.IsAnonymous ? null : (int?)CurrentUser.Id,
                            ip = GetRemoteIP(),
                            creation = saveTime = DateTime.UtcNow
                        }
                    ).First();

                    if (parent == null)
                    {
                        SaveMetadata(saveId, title, description, true);
                    }
                }
                else
                {
                    queryId = query.Id;
                }

                QueryRunner.LogQueryExecution(CurrentUser, siteId, queryId);

                // Need to fix up the way we pass back results
                results.QueryId = saveId;
                // Consider handling this XSS condition (?) in the ToJson() method instead, if possible
                response = Content(results.ToJson().Replace("/", "\\/"), "application/json");
            }
            catch (Exception ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }

        [HttpPost]
        [Route(@"query/run/{siteId:\d+}/{revisionId:\d+}")]
        public ActionResult Execute(int revisionId, int siteId, bool? textResults, bool? executionPlan, bool? crossSite, bool? excludeMetas)
        {
            ActionResult response = null;

            try
            {
                var query = GetQueryForRevision(revisionId);

                if (query == null)
                {
                    throw new ApplicationException("Invalid revision ID");
                }

                var parsedQuery = new ParsedQuery(
                    query.QueryBody,
                    Request.Params,
                    executionPlan == true,
                    crossSite == true,
                    excludeMetas == true
                );

                var results = ExecuteWithResults(parsedQuery, siteId, textResults == true);
                QueryRunner.LogQueryExecution(CurrentUser, siteId, query.Id);

                // Consider handling this XSS condition (?) in the ToJson() method instead, if possible
                response = Content(results.ToJson().Replace("/", "\\/"), "application/json");
            }
            catch (Exception ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }


        [HttpPost]
        [Route(@"query/update/{revisionId:\d+}")]
        public ActionResult UpdateMetadata(int revisionId, string title, string description)
        {
            ActionResult response = null;

            try
            {
                Revision revision = GetBasicRevision(revisionId);

                if (revision == null)
                {
                    throw new ApplicationException("Invalid revision ID");
                }

                if (revision.RootId.HasValue)
                {
                    revisionId = revision.RootId.Value;
                }

                SaveMetadata(revisionId, title, description, false);
            }
            catch (ApplicationException ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }

        [Route(@"{sitename}/csv/{queryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowSingleSiteCsv(string sitename, int queryId)
        {
            Query query = FindQuery(queryId);

            if (query == null)
            {
                return PageNotFound();
            }

            TrackQueryView(queryId);
            CachedResult cachedResults = GetCachedResults(query);
            return new CsvResult(cachedResults.Results);
        }

        [Route(@"{sitename}/mcsv/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowMultiSiteCsv(string sitename, int revisionId)
        {
            Query query = GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }
            
            var json = QueryRunner.GetMultiSiteResults(
                new ParsedQuery(query.QueryBody, Request.Params, true, false),
                CurrentUser
            ).ToJson();

            return new CsvResult(json);
        }

        [Route(@"{sitename}/nmcsv/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowMultiSiteWithoutMetaCsv(string sitename, int revisionId)
        {
            Query query = GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }

            var json = QueryRunner.GetMultiSiteResults(
                new ParsedQuery(query.QueryBody, Request.Params, true, true),
                CurrentUser
            ).ToJson();

            return new CsvResult(json);
        }

        [Route(@"{sitename}/qte/{savedQueryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult EditText(string sitename, int savedQueryId)
        {
            bool foundSite = SetCommonQueryViewData(sitename);
            if (!foundSite)
            {
                return PageNotFound();
            }

            SetHeaderInfo(savedQueryId);

            SavedQuery savedQuery = FindSavedQuery(savedQueryId);

            if (savedQuery == null)
            {
                return PageNotFound();
            }

            savedQuery.UpdateQueryBodyComment();

            ViewData["query"] = savedQuery.Query;

            CachedResult cachedResults = GetCachedResults(savedQuery.Query);

            if (cachedResults != null && cachedResults.Results != null)
            {
                cachedResults.Results = QueryResults.FromJson(cachedResults.Results).ToTextResults().ToJson();
            }

            ViewData["cached_results"] = cachedResults;

            return View("New", Site);
        }

        [Route(@"{sitename}/qe/{savedQueryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult Edit(string sitename, int savedQueryId)
        {
            bool foundSite = SetCommonQueryViewData(sitename);
            if (!foundSite)
            {
                return PageNotFound();
            }

            SetHeaderInfo(savedQueryId);

            SavedQuery savedQuery = FindSavedQuery(savedQueryId);

            if (savedQuery == null)
            {
                return PageNotFound();
            }

            savedQuery.UpdateQueryBodyComment();

            ViewData["query"] = savedQuery.Query;
            ViewData["cached_results"] = GetCachedResults(savedQuery.Query);

            return View("New", Site);
        }


        [Route(@"{sitename}/qt/{queryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowText(string sitename, int queryId)
        {
            bool foundSite = SetCommonQueryViewData(sitename);
            if (!foundSite)
            {
                return PageNotFound();
            }

            Query query = FindQuery(queryId);
            if (query == null)
            {
                return PageNotFound();
            }

            TrackQueryView(queryId);

            ViewData["query"] = query;
            CachedResult cachedResults = GetCachedResults(query);
            if (cachedResults != null && cachedResults.Results != null)
            {
                cachedResults.Results = QueryResults.FromJson(cachedResults.Results).ToTextResults().ToJson();
            }

            ViewData["cached_results"] = cachedResults;
            return View("New", Site);
        }

        [Route(@"{sitename}/q/{queryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult Show(string sitename, int queryId)
        {
            bool foundSite = SetCommonQueryViewData(sitename);
            if (!foundSite)
            {
                return PageNotFound();
            }

            Query query = FindQuery(queryId);
            if (query == null)
            {
                return PageNotFound();
            }

            ViewData["query"] = query;
            TrackQueryView(queryId);
            ViewData["cached_results"] = GetCachedResults(query);
            return View("New", Site);
        }

        /// <summary>
        /// Download a query execution plan as xml.
        /// </summary>
        [Route(@"{sitename}/plan/{queryId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowPlan(string sitename, int queryId)
        {
            Query query = FindQuery(queryId);
            if (query == null)
            {
                return PageNotFound();
            }

            CachedPlan cachedPlan = GetCachedPlan(query);
            if (cachedPlan == null)
            {
                return PageNotFound();
            }

            return new QueryPlanResult(cachedPlan.Plan);
        }

        [Route("{sitename}/query/new", RoutePriority.Low)]
        public ActionResult New(string sitename)
        {
            bool foundSite = SetCommonQueryViewData(sitename);
            
            return foundSite?View(Site):PageNotFound();
        }

        private QueryResults ExecuteWithResults(ParsedQuery query, int siteId, bool textResults)
        {
            QueryResults results = null;

            if (!query.AllParamsSet)
            {
                throw new ApplicationException(!string.IsNullOrEmpty(query.ErrorMessage) ?
                    query.ErrorMessage : "All parameters must be set!");
            }

            Site site = GetSite(siteId);

            if (site == null)
            {
                throw new ApplicationException("Invalid site ID");
            }

            if (!query.IsCrossSite)
            {
                results = QueryRunner.GetSingleSiteResults(query, site, CurrentUser);
            }
            else
            {
                results = QueryRunner.GetMultiSiteResults(query, CurrentUser);
                textResults = true;
            }

            if (textResults)
            {
                results = results.ToTextResults();
            }

            if (query.HasExecutionPlan)
            {
                results = results.TransformQueryPlan();
            }

            return results;
        }

        private ActionResult TransformExecutionException(Exception ex)
        {
            var response = new Dictionary<string, string>();
            var sqlex = ex as SqlException;

            if (sqlex != null)
            {
                response["errorLine"] = sqlex.LineNumber.ToString();
            }

            response["error"] = ex.Message;

            return Json(response);
        }

        private Query GetQueryForRevision(int revisionId)
        {
            return Current.DB.Query<Query>(@"
                SELECT
                    *
                FROM
                    Queries JOIN
                    Revisions ON Queries.Id = Revisions.QueryId AND Revisions.Id = @revision
                ",
                new
                {
                    revision = revisionId
                }
            ).FirstOrDefault();
        }

        private Revision GetBasicRevision(int revisionId)
        {
            return Current.DB.Query<Revision>(
                "SELECT * FROM Revisions WHERE Id = @revision",
                new
                {
                    revision = revisionId
                }
            ).FirstOrDefault();
        }

        private Revision GetCompleteRevision(int revisionId)
        {
            return Current.DB.Query<Revision, Query, Metadata, Revision>(@"
                SELECT
                    *
                FROM
                    Revisions revision JOIN
                    Queries query ON query.Id = revision.QueryId AND revision.Id = @revision JOIN
                    Metadata metadata ON metadata.Id = @revision OR metadata.Id = revision.RootId
                ",
                (revision, query, metadata) =>
                {
                    revision.Query = query;
                    revision.Metadata = metadata;

                    return revision;
                },
                new
                {
                    revision = revisionId
                }
            ).FirstOrDefault();
        }

        private void SaveMetadata(int rootId, string title, string description, bool isNew)
        {
            if (isNew)
            {
                Current.DB.Execute(@"
                    INSERT INTO Metadata(
                        Id, Title, Description
                    ) VALUES(
                        @id, @title, @description
                    )",
                    new
                    {
                        id = rootId,
                        title = title,
                        description = description
                    }
                );
            }
            else
            {
                // Currently we only allow the author of the original revision to change
                // the metadata, but people might be annoyed by this inflexibility...
                int isOwner = Current.DB.Query<int>(@"
                    SELECT
                        COUNT(Id)
                    FROM
                        Revisions
                    WHERE
                        Id = @revision AND
                        CreatorId = @user",
                    new
                    {
                        revision = rootId,
                        user = CurrentUser.Id
                    }
                ).First();

                if (isOwner == 0)
                {
                    throw new ApplicationException("You are not the owner!");
                }

                Current.DB.Execute(@"
                    UPDATE
                        Metadata
                    SET
                        Title = @title,
                        Description = @description
                    WHERE
                        Id = @revision",
                    new
                    {
                        title = title,
                        description = description,
                        revision = rootId
                    }
                );
            }
        }

        private bool SetCommonQueryViewData(string sitename)
        {
            SetHeaderInfo();
            var s = GetSite(sitename);
            if (s==null)
            {
                return false;
            }
            Site = s;
            SelectMenuItem("Compose Query");

            ViewData["GuessedUserId"] = Site.GuessUserId(CurrentUser);
            ViewData["Tables"] = Site.GetTableInfos();
            ViewData["Sites"] = Current.DB.Sites.ToList();

            return true;
        }

        private void TrackQueryView(int id)
        {
            if (!IsSearchEngine())
            {
                QueryViewTracker.TrackQueryView(GetRemoteIP(), id);
            }
        }


        private void SetHeaderInfo()
        {
            SetHeaderInfo(null);
        }

        private Query FindQuery(int id)
        {
            return Current.DB.Queries.FirstOrDefault(q => q.Id == id);
        }

        private SavedQuery FindSavedQuery(int id)
        {
            return Current.DB.SavedQueries.FirstOrDefault(s => s.Id == id);
        }

        private void SetHeaderInfo(int? edit)
        {
            if (edit != null)
            {
                SetHeader("Editing Query");
                ViewData["SavedQueryId"] = edit.Value;
            }
            else
            {
                SetHeader("Compose Query");
            }
        }
    }
}