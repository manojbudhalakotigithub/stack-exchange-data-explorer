﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

using StackExchange.DataExplorer.Helpers;
using StackExchange.DataExplorer.Models;

namespace StackExchange.DataExplorer.Helpers
{
    public class QueryUtil
    {
        /// <summary>
        /// Retrieves the basic revision information
        /// </summary>
        /// <param name="revisionId">The ID of the revision</param>
        /// <returns>A revision, or null if the ID was invalid</returns>
        public static Revision GetBasicRevision(int revisionId)
        {
            return Current.DB.Query<Revision>(
                "SELECT * FROM Revisions WHERE Id = @revision",
                new
                {
                    revision = revisionId
                }
            ).FirstOrDefault();
        }

        /// <summary>
        /// Retrieves the cached results for the given query
        /// </summary>
        /// <param name="query">The query to retrieve cached results for</param>
        /// <param name="siteId">The site ID that the query is run against</param>
        /// <returns>The cached results, or null if no results exist in the cache</returns>
        public static CachedResult GetCachedResults(ParsedQuery query, int siteId)
        {
            if (query == null || !query.AllParamsSet)
            {
                return null;
            }

            return Current.DB.Query<CachedResult>(@"
                SELECT
                    *
                FROM
                    CachedResults
                WHERE
                    QueryHash = @hash AND
                    SiteId = @site",
                new
                {
                    hash = query.ExecutionHash,
                    site = siteId
                }
            ).FirstOrDefault();
        }

        /// <summary>
        /// Retrieves a Revision with its corresponding Query and Metadata information
        /// </summary>
        /// <param name="revisionId">The ID of the revision</param>
        /// <returns>The revision, or null if the ID was invalid</returns>
        public static Revision GetCompleteRevision(int revisionId)
        {
            return Current.DB.Query<Revision, Query, Metadata, Revision>(@"
                SELECT
                    *
                FROM
                    Revisions revision JOIN
                    Queries query ON query.Id = revision.QueryId AND revision.Id = @revision JOIN
                    Metadata metadata ON metadata.RevisionId = @revision OR metadata.RevisionId = revision.RootId
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

        /// <summary>
        /// Retrieves the Revision that represents the "featurable" version of a particular
        /// user's edits on a given query lineage. Basically, this is either the user's most
        /// recent revision, or the revision they marked as being the one to feature.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="rootId"></param>
        /// <returns></returns>
        public static Revision GetFeaturedCompleteRevision(int userId, int rootId)
        {
            return Current.DB.Query<Revision, Query, Metadata, Revision>(@"
                SELECT
                    *
                FROM
                    Revisions revision
                JOIN
                    Queries query
                ON
                    query.Id = revision.QueryId AND
                    revision.OwnerId = @owner AND
                    (revision.RootId = @root OR revision.Id = @root)
                JOIN
                    Metadata metadata
                ON
                    metadata.RevisionId = @root OR metadata.RevisionId = revision.RootId
                ORDER BY
                    revision.IsFeature DESC, revision.CreationDate DESC
                ",
                (revision, query, metadata) =>
                {
                    revision.Query = query;
                    revision.Metadata = metadata;

                    return revision;
                },
                new
                {
                    owner = userId,
                    root = rootId
                }
            ).FirstOrDefault();
        }

        /// <summary>
        /// Retrieves the Query linked to the provided revision
        /// </summary>
        /// <param name="revisionId">The ID of the revision</param>
        /// <returns>Linked query, or null if the ID was invalid</returns>
        public static Query GetQueryForRevision(int revisionId)
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
    }
}