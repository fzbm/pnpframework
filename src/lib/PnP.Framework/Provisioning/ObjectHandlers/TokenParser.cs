﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Online.SharePoint.TenantAdministration;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;
using Newtonsoft.Json;
using PnP.Framework.ALM;
using PnP.Framework.Diagnostics;
using PnP.Framework.Graph;
using PnP.Framework.Provisioning.Connectors;
using PnP.Framework.Provisioning.Model;
using PnP.Framework.Provisioning.ObjectHandlers.TokenDefinitions;
using PnP.Framework.Utilities;
using TermGroup = Microsoft.SharePoint.Client.Taxonomy.TermGroup;

namespace PnP.Framework.Provisioning.ObjectHandlers
{
    /// <summary>
    /// Handles methods for token parser
    /// </summary>
    public class TokenParser : ICloneable
    {
        private Web _web;
        private List<TokenDefinition> _tokens;
        private Dictionary<string, string> _tokenDictionary;
        private Dictionary<string, TokenDefinition> _nonCacheableTokenDictionary;
        private Dictionary<string, TokenDefinition> _listTokenDictionary;
        private bool _loadSiteCollectionTermGroups;

        private readonly Dictionary<string, string> _listsTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _initializedFromHierarchy;
        private readonly Action<TokenDefinition> _addTokenWithCacheUpdateDelegate;
        private readonly Action<TokenDefinition> _addTokenToListDelegate;

        // First group supports tokens in form '{param:value}' , second group supports nested parameters in form '{param:{xxx..'
        private static readonly Regex ReToken = new Regex(@"(?:(\{(?:\1??[^{]*?\})))|(?:(\{(?:\1??[^{]*?:)))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReTokenFallback = new Regex(@"\{.*?\}", RegexOptions.Compiled);
        private static readonly Regex ReGuid = new Regex("(?<guid>\\{\\S{8}-\\S{4}-\\S{4}-\\S{4}-\\S{12}?\\})", RegexOptions.Compiled);
        private static readonly char[] InternalTokenDelimiters = { ':' };
        private static readonly char[] TokenChars = { '{', '~' };
        private static readonly char[] TokenBoundaryChars = { '{', '}' };
        private static readonly char[] UrlSeparators = { '/' };

        /// <summary>
        /// List of token definitions
        /// </summary>
        public List<TokenDefinition> Tokens
        {
            get { return _tokens; }
            private set
            {
                _tokens = value;
            }
        }

        /// <summary>
        /// adds token definition
        /// </summary>
        /// <param name="tokenDefinition">A TokenDefinition object</param>
        public void AddToken(TokenDefinition tokenDefinition)
        {
            AddTokenWithCacheUpdate(tokenDefinition);
        }

        // Lightweight rebase
        public void Rebase(Web web)
        {
            foreach (var token in _tokens)
            {
                token.ClearCache();
            }
        }

        // Heavy rebase for switching templates
        public void Rebase(Web web, ProvisioningTemplate template, ProvisioningTemplateApplyingInformation applyingInformation = null)
        {
            var tokenIds = ParseTemplate(template);
            _web = web;
            _loadSiteCollectionTermGroups = applyingInformation?.LoadSiteCollectionTermGroups ?? true;

            foreach (var token in _tokens.OfType<VolatileTokenDefinition>())
            {
                token.ClearVolatileCache(web);
            }

            _tokens.RemoveAll(t => t is SiteToken);
            _tokens.Add(new SiteToken(web));

            // remove list tokens
            if (tokenIds.Contains("listid")
                || tokenIds.Contains("listurl")
                || tokenIds.Contains("viewid")
                || tokenIds.Contains("listcontenttypeid"))
            {
                RebuildListTokens(web, _addTokenToListDelegate);
            }

            // remove content type tokens
            if (tokenIds.Contains("contenttypeid"))
                AddContentTypeTokens(web);

            // remove field tokens
            if (tokenIds.Contains("fieldid"))
            {
                _tokens.RemoveAll(t => t is FieldTitleToken || t is FieldIdToken);
                AddFieldTokens(web);
            }

            // remove group tokens
            if (tokenIds.Contains("groupid")
                || tokenIds.FindIndex(t => t.StartsWith("associated", StringComparison.OrdinalIgnoreCase)) > -1)
            {
                _tokens.RemoveAll(t => t is GroupIdToken || t is AssociatedGroupToken);
                AddGroupTokens(web);
            }

            // remove role definition tokens
            if (tokenIds.Contains("roledefinition"))
            {
                _tokens.RemoveAll(t => t is RoleDefinitionToken || t is RoleDefinitionIdToken);
                AddRoleDefinitionTokens(web);
            }

            CalculateTokenCount(_tokens, out int cacheableCount, out int nonCacheableCount);
            BuildTokenCache(cacheableCount, nonCacheableCount);
        }

        /// <summary>
        /// Creates a new TokenParser. Only to be used by Clone().
        /// </summary>
        /// <param name="tokens">The list with TokenDefinitions to copy over</param>
        /// <param name="web">The Web context to copy over</param>
        /// <param name="loadSiteCollectionTermGroups">Specifies if site collection term groups should be loaded.</param>
        private TokenParser(Web web, List<TokenDefinition> tokens, bool loadSiteCollectionTermGroups)
        {
            _web = web;
            _tokens = tokens;
            _addTokenWithCacheUpdateDelegate = AddTokenWithCacheUpdate;
            _addTokenToListDelegate = AddTokenToList;
            _loadSiteCollectionTermGroups = loadSiteCollectionTermGroups;

            CalculateTokenCount(_tokens, out int cacheableCount, out int nonCacheableCount);
            BuildTokenCache(cacheableCount, nonCacheableCount);
        }

        public TokenParser(Tenant tenant, ProvisioningHierarchy hierarchy) :
            this(tenant, hierarchy, null)
        {
        }

        public TokenParser(Tenant tenant, ProvisioningHierarchy hierarchy, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            _addTokenWithCacheUpdateDelegate = AddTokenWithCacheUpdate;
            _addTokenToListDelegate = AddTokenToList;
            _loadSiteCollectionTermGroups = applyingInformation?.LoadSiteCollectionTermGroups ?? true;

            // CHANGED: To avoid issues with low privilege users
            Web web;
            if (TenantExtensions.IsCurrentUserTenantAdmin((ClientContext)tenant.Context))
            {
                web = ((ClientContext)tenant.Context).Web;
            }
            else
            {
                var rootSiteUrl = tenant.Context.Url.Replace("-admin", string.Empty);
                var context = ((ClientContext)tenant.Context).Clone(rootSiteUrl);
                web = context.Web;
            }

            web.EnsureProperties(w => w.ServerRelativeUrl, w => w.Url, w => w.Language);

            _web = web;
            _tokens = new List<TokenDefinition>();

            foreach (KeyValuePair<string, string> parameter in hierarchy.Parameters)
            {
                _tokens.Add(new ParameterToken(null, parameter.Key, parameter.Value ?? string.Empty));
            }

            _tokens.Add(new GuidToken(null));
            _tokens.Add(new CurrentUserIdToken(web));
            _tokens.Add(new CurrentUserLoginNameToken(web));
            _tokens.Add(new CurrentUserFullNameToken(web));
            _tokens.Add(new AuthenticationRealmToken(web));
            _tokens.Add(new HostUrlToken(web));
            _tokens.Add(new FqdnToken(web));

            AddResourceTokens(web, hierarchy.Localizations, hierarchy.Connector);

            CalculateTokenCount(_tokens, out int cacheableCount, out int nonCacheableCount);
            BuildTokenCache(cacheableCount, nonCacheableCount);

            _initializedFromHierarchy = true;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="web">A SharePoint site or subsite</param>
        /// <param name="template">a provisioning template</param>
        public TokenParser(Web web, ProvisioningTemplate template) :
            this(web, template, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="web">A SharePoint site or subsite</param>
        /// <param name="template">a provisioning template</param>
        /// <param name="applyingInformation">The provisioning template applying information</param>
        public TokenParser(Web web, ProvisioningTemplate template, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            var tokenIds = ParseTemplate(template);

            web.EnsureProperties(w => w.ServerRelativeUrl, w => w.Url, w => w.Language);

            _web = web;
            _tokens = new List<TokenDefinition>();
            _addTokenWithCacheUpdateDelegate = AddTokenWithCacheUpdate;
            _addTokenToListDelegate = AddTokenToList;
            _loadSiteCollectionTermGroups = applyingInformation?.LoadSiteCollectionTermGroups ?? true;

            if (tokenIds.Contains("sitecollection"))
                _tokens.Add(new SiteCollectionToken(web));
            if (tokenIds.Contains("sitecollectionid"))
                _tokens.Add(new SiteCollectionIdToken(web));
            if (tokenIds.Contains("sitecollectionidencoded"))
                _tokens.Add(new SiteCollectionIdEncodedToken(web));
            if (tokenIds.Contains("site"))
                _tokens.Add(new SiteToken(web));
            if (tokenIds.Contains("masterpagecatalog"))
                _tokens.Add(new MasterPageCatalogToken(web));
            if (tokenIds.Contains("sitecollectiontermstoreid"))
                _tokens.Add(new SiteCollectionTermStoreIdToken(web));
            if (tokenIds.Contains("keywordstermstoreid"))
                _tokens.Add(new KeywordsTermStoreIdToken(web));
            if (tokenIds.Contains("themecatalog"))
                _tokens.Add(new ThemeCatalogToken(web));
            if (tokenIds.Contains("webname"))
                _tokens.Add(new WebNameToken(web));
            if (tokenIds.Contains("siteid"))
                _tokens.Add(new SiteIdToken(web));
            if (tokenIds.Contains("siteidencoded"))
                _tokens.Add(new SiteIdEncodedToken(web));
            if (tokenIds.Contains("siteowner"))
                _tokens.Add(new SiteOwnerToken(web));
            if (tokenIds.Contains("sitetitle") || tokenIds.Contains("sitename"))
                _tokens.Add(new SiteTitleToken(web));
            if (tokenIds.Contains("groupsitetitle") || tokenIds.Contains("groupsitename"))
                _tokens.Add(new GroupSiteTitleToken(web));
            if (tokenIds.Contains("associatedownergroupid"))
                _tokens.Add(new AssociatedGroupIdToken(web, AssociatedGroupIdToken.AssociatedGroupType.owners));
            if (tokenIds.Contains("associatedmembergroupid"))
                _tokens.Add(new AssociatedGroupIdToken(web, AssociatedGroupIdToken.AssociatedGroupType.members));
            if (tokenIds.Contains("associatedvisitorgroupid"))
                _tokens.Add(new AssociatedGroupIdToken(web, AssociatedGroupIdToken.AssociatedGroupType.visitors));
            if (tokenIds.Contains("associatedownergroup"))
                _tokens.Add(new AssociatedGroupToken(web, AssociatedGroupToken.AssociatedGroupType.owners));
            if (tokenIds.Contains("associatedmembergroup"))
                _tokens.Add(new AssociatedGroupToken(web, AssociatedGroupToken.AssociatedGroupType.members));
            if (tokenIds.Contains("associatedvisitorgroup"))
                _tokens.Add(new AssociatedGroupToken(web, AssociatedGroupToken.AssociatedGroupType.visitors));
            if (tokenIds.Contains("guid"))
                _tokens.Add(new GuidToken(web));
            if (tokenIds.Contains("now"))
                _tokens.Add(new DateNowToken(web));
            if (tokenIds.Contains("currentuserid"))
                _tokens.Add(new CurrentUserIdToken(web));
            if (tokenIds.Contains("currentuserloginname"))
                _tokens.Add(new CurrentUserLoginNameToken(web));
            if (tokenIds.Contains("currentuserfullname"))
                _tokens.Add(new CurrentUserFullNameToken(web));
            if (tokenIds.Contains("authenticationrealm"))
                _tokens.Add(new AuthenticationRealmToken(web));
            if (tokenIds.Contains("hosturl"))
                _tokens.Add(new HostUrlToken(web));
            if (tokenIds.Contains("fqdn"))
                _tokens.Add(new FqdnToken(web));
            if (tokenIds.Contains("sitecollectionconnectedoffice365groupid"))
                _tokens.Add(new SiteCollectionConnectedOffice365GroupId(web));
            if (tokenIds.Contains("everyone"))
                _tokens.Add(new EveryoneToken(web));
            if (tokenIds.Contains("everyonebutexternalusers"))
                _tokens.Add(new EveryoneButExternalUsersToken(web));

            if (tokenIds.Contains("listid") || tokenIds.Contains("listurl") || tokenIds.Contains("viewid"))
                RebuildListTokens(web, _addTokenToListDelegate);
            if (tokenIds.Contains("contenttypeid"))
                AddContentTypeTokens(web);

            if (!_initializedFromHierarchy && tokenIds.Contains("parameter"))
            {
                // Add parameters
                foreach (var parameter in template.Parameters)
                {
                    _tokens.Add(new ParameterToken(web, parameter.Key, parameter.Value ?? string.Empty));
                }
            }

            if (tokenIds.Contains("sitedesignid"))
                AddSiteDesignTokens(web, applyingInformation);
            if (tokenIds.Contains("sitescriptid"))
                AddSiteScriptTokens(web, applyingInformation);
            if (tokenIds.Contains("storageentityvalue"))
                AddStorageEntityTokens(web);

            // Fields
            if (tokenIds.Contains("fieldtitle") || tokenIds.Contains("fieldid"))
                AddFieldTokens(web);

            // Handle resources
            if (tokenIds.Contains("loc") || tokenIds.Contains("localize") || tokenIds.Contains("localization") || tokenIds.Contains("resource") || tokenIds.Contains("res"))
                AddResourceTokens(web, template.Localizations, template.Connector);

            // OOTB Roledefs
            if (tokenIds.Contains("roledefinition") || tokenIds.Contains("roledefinitionid"))
                AddRoleDefinitionTokens(web);

            // Groups
            if (tokenIds.Contains("groupid"))
                AddGroupTokens(web);

            // AppPackages tokens
            if (tokenIds.Contains("apppackageid"))
                AddAppPackagesTokens(web);
            if (tokenIds.Contains("pageuniqueid"))
                AddPageUniqueIdTokens(web);
            if (tokenIds.Contains("propertybagvalue"))
                AddPropertyBagTokens(web);

            // TermStore related tokens
            AddTermStoreTokens(web, tokenIds);

            CalculateTokenCount(_tokens, out int cacheableCount, out int nonCacheableCount);
            BuildTokenCache(cacheableCount, nonCacheableCount);
        }

        private void AddResourceTokens(Web web, LocalizationCollection localizations, FileConnectorBase connector)
        {
            if (localizations == null || localizations.Count == 0)
            {
                return;
            }

            if (connector == null)
            {
                throw new ArgumentNullException(nameof(connector), "Template or Hierarchy File Connector cannot be null");
            }

            //https://github.com/SharePoint/PnP-Provisioning-Schema/issues/301
            //fixing issue to allow multiple resx files in the template. i.e:
            //<pnp:Localization LCID="1033" Name="core" ResourceFile="core.en-us.resx" />
            //<pnp:Localization LCID="3082" Name="core" ResourceFile="core.es-es.resx" />
            //<pnp:Localization LCID="1033" Name="intranet" ResourceFile="intranet.en-us.resx" />
            //<pnp:Localization LCID="3082" Name="intranet" ResourceFile="intranet.es-es.resx" />

            var resourceEntries = new Dictionary<string, List<ResourceEntry>>(StringComparer.InvariantCulture);

            foreach (var localizationEntry in localizations)
            {
                string filePath = localizationEntry.ResourceFile;
                int lcid = localizationEntry.LCID;

                if (filePath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
                {
                    using (var stream = connector.GetFileStream(filePath))
                    {
                        if (stream == null)
                        {
                            continue;
                        }

                        // Use raw XML approach as the .Net Framework resxreader seems to choke on some resx files
                        // TODO: research this!

                        var xElement = XElement.Load(stream);
                        foreach (var dataElement in xElement.Descendants("data"))
                        {
                            var key = dataElement.Attribute("name").Value;
                            var value = dataElement.Descendants().First().Value;
                            string escapedValue = value.Replace("\"", "&quot;");

                            AddResourceEntry($"{localizationEntry.Name}:{key}", lcid, escapedValue, resourceEntries);
                            AddResourceEntry(key, lcid, escapedValue, resourceEntries);
                        }

                        /*
                            using (ResXResourceReader resxReader = new ResXResourceReader(stream))
                            {
                                foreach (DictionaryEntry entry in resxReader)
                                {
                                    // One can have multiple resource files in a single file, by adding tokens with resource file name and without we allow both scenarios to resolve
                                    resourceEntries.Add(new Tuple<string, uint, string>($"{localizationEntry.Name}:{entry.Key}", (uint)localizationEntry.LCID, entry.Value.ToString().Replace("\"", "&quot;")));
                                    resourceEntries.Add(new Tuple<string, uint, string>(entry.Key.ToString(), (uint)localizationEntry.LCID, entry.Value.ToString().Replace("\"", "&quot;")));
                                }
                            }
                                */
                    }
                }
                else if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var jsonString = connector.GetFile(filePath);
                    if (string.IsNullOrEmpty(jsonString))
                    {
                        continue;
                    }

                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
                    foreach (KeyValuePair<string, string> entry in dict)
                    {
                        string escapedValue = entry.Value.Replace("\"", "&quot;");

                        AddResourceEntry($"{localizationEntry.Name}:{entry.Key}", lcid, escapedValue, resourceEntries);
                        AddResourceEntry(entry.Key, lcid, escapedValue, resourceEntries);
                    }
                }
            }

            // Ensure an additional capacity of at least the amount of tokens we want to add.
            _tokens.Capacity = _tokens.Count + resourceEntries.Count + 1;

            foreach (KeyValuePair<string, List<ResourceEntry>> pair in resourceEntries)
            {
                _tokens.Add(new LocalizationToken(web, pair.Key, pair.Value, localizations.DefaultLCID));
            }
        }

        private void AddFieldTokens(Web web)
        {
            _tokens.RemoveAll(t => t is FieldTitleToken || t is FieldIdToken);

            // Add all the site columns
            var fields = web.AvailableFields;

            web.Context.Load(fields, flds => flds.Include(f => f.Title, f => f.InternalName, f => f.Id));
            web.Context.ExecuteQueryRetry();

            foreach (var field in fields)
            {
                _tokens.Add(new FieldTitleToken(web, field.InternalName, field.Title));
                _tokens.Add(new FieldIdToken(web, field.InternalName, field.Id));
            }

            // Add all the list columns
            //var lists = web.Lists;
            //web.Context.Load(lists, list => list.Include(listField => listField.Fields.Include(field => field.Title, field => field.InternalName, field => field.Id)));
            //web.Context.ExecuteQueryRetry();
            //foreach (var list in lists)
            //{
            //    foreach (var field in list.Fields)
            //    {
            //        _tokens.Add(new FieldTitleToken(web, field.InternalName, field.Title));
            //        _tokens.Add(new FieldIdToken(web, field.InternalName, field.Id));
            //    }
            //}

            //if (web.IsSubSite())
            //{
            //    // SiteColumns from rootsite
            //    var rootWeb = (web.Context as ClientContext).Site.RootWeb;
            //    var siteColumns = rootWeb.Fields;
            //    web.Context.Load(siteColumns, flds => flds.Include(f => f.Title, f => f.InternalName));
            //    web.Context.ExecuteQueryRetry();
            //    foreach (var field in siteColumns)
            //    {
            //        _tokens.Add(new FieldTitleToken(rootWeb, field.InternalName, field.Title));
            //    }
            //}
        }

        private void AddRoleDefinitionTokens(Web web)
        {
            web.EnsureProperty(w => w.RoleDefinitions.Include(r => r.RoleTypeKind, r => r.Name, r => r.Id));

            foreach (var roleDef in web.RoleDefinitions.AsEnumerable().Where(r => r.RoleTypeKind != RoleType.None))
            {
                _tokens.Add(new RoleDefinitionToken(web, roleDef));
            }

            foreach (var roleDef in web.RoleDefinitions)
            {
                _tokens.Add(new RoleDefinitionIdToken(web, roleDef.Name, roleDef.Id));
            }
        }

        private void AddPropertyBagTokens(Web web)
        {
            web.EnsureProperty(w => w.AllProperties);

            foreach (var keyValue in web.AllProperties.FieldValues)
            {
                _tokens.Add(new PropertyBagValueToken(web, keyValue.Key, keyValue.Value.ToString()));
            }
        }

        private void AddGroupTokens(Web web)
        {
            web.EnsureProperty(w => w.SiteGroups.Include(g => g.Title, g => g.Id));

            foreach (var siteGroup in web.SiteGroups)
            {
                _tokens.Add(new GroupIdToken(web, siteGroup.Title, siteGroup.Id.ToString()));
            }

            web.EnsureProperty(w => w.AssociatedVisitorGroup).EnsureProperties(g => g.Id, g => g.Title);
            web.EnsureProperty(w => w.AssociatedMemberGroup).EnsureProperties(g => g.Id, g => g.Title);
            web.EnsureProperty(w => w.AssociatedOwnerGroup).EnsureProperties(g => g.Id, g => g.Title);

            if (!web.AssociatedVisitorGroup.ServerObjectIsNull.Value)
            {
                _tokens.Add(new GroupIdToken(web, "associatedvisitorgroup", web.AssociatedVisitorGroup.Id.ToString()));
            }

            if (!web.AssociatedMemberGroup.ServerObjectIsNull.Value)
            {
                _tokens.Add(new GroupIdToken(web, "associatedmembergroup", web.AssociatedMemberGroup.Id.ToString()));
            }

            if (!web.AssociatedOwnerGroup.ServerObjectIsNull.Value)
            {
                _tokens.Add(new GroupIdToken(web, "associatedownergroup", web.AssociatedOwnerGroup.Id.ToString()));
            }

            if (PnPProvisioningContext.Current?.AcquireTokenAsync == null)
            {
                return;
            }

            var microsoftGraphBaseUri = AuthenticationManager.GetGraphBaseEndPoint(web.Context.GetAzureEnvironment());

            var accessToken = PnPProvisioningContext.Current.AcquireToken(microsoftGraphBaseUri.Authority, "Group.Read.All");
            if (string.IsNullOrEmpty(accessToken))
            {
                return;
            }

            try
            {
                // Get Office 365 Groups
                var officeGroups = UnifiedGroupsUtility.GetUnifiedGroups(accessToken, includeSite: false);
                foreach (var group in officeGroups)
                {
                    _tokens.Add(new O365GroupIdToken(web, group.DisplayName, group.GroupId));
                    if (!group.DisplayName.Equals(group.MailNickname))
                    {
                        _tokens.Add(new O365GroupIdToken(web, group.MailNickname, group.GroupId));
                    }
                }
            }
            catch (Microsoft.Graph.ServiceException ex)
            {
                // If we don't have permission to access the O365 groups, just skip it
                Log.Warning(Constants.LOGGING_SOURCE, CoreResources.GraphExtensions_ErrorOccured, ex.Error.Message);
            }
        }

        private void AddTermStoreTokens(Web web, List<string> tokenIds)
        {
            bool siteCollectionTermSetIdTokenFound = tokenIds.Contains("sitecollectiontermsetid");

            if (!tokenIds.Contains("termstoreid")
                && !tokenIds.Contains("termsetid")
                && !tokenIds.Contains("sitecollectiontermgroupid")
                && !tokenIds.Contains("sitecollectiontermgroupname")
                && !siteCollectionTermSetIdTokenFound)
            {
                return;
            }

            TaxonomySession session = TaxonomySession.GetTaxonomySession(web.Context);

            if (tokenIds.Contains("termstoreid"))
            {
                var termStores = session.EnsureProperty(s => s.TermStores);
                foreach (var ts in termStores)
                {
                    _tokens.Add(new TermStoreIdToken(web, ts.Name, ts.Id));
                }
            }

            var termStore = session.GetDefaultSiteCollectionTermStore();
            web.Context.Load(termStore);
            web.Context.ExecuteQueryRetry();

            if (tokenIds.Contains("termsetid") && !termStore.ServerObjectIsNull.Value)
            {
                IEnumerable<TermGroup> termGroups;

                if (_loadSiteCollectionTermGroups)
                {
                    termGroups = termStore.Groups;

                    web.Context.Load(
                        termStore.Groups,
                        groups => groups.Include(
                            group => group.Name,
                            group => group.TermSets.Include(
                                termSet => termSet.Name,
                                termSet => termSet.Id)
                        ));
                }
                else
                {
                    termGroups = web.Context.LoadQuery(termStore
                        .Groups
                        .Where(group => !group.IsSiteCollectionGroup)
                        .Include(
                            group => group.Name,
                            group => group.TermSets.Include(
                                termSet => termSet.Name,
                                termSet => termSet.Id)));
                }

                web.Context.ExecuteQueryRetry();

                foreach (var termGroup in termGroups)
                {
                    foreach (var termSet in termGroup.TermSets)
                    {
                        _tokens.Add(new TermSetIdToken(web, termGroup.Name, termSet.Name, termSet.Id));
                    }
                }
            }

            if (tokenIds.Contains("sitecollectiontermgroupid"))
                _tokens.Add(new SiteCollectionTermGroupIdToken(web));

            if (tokenIds.Contains("sitecollectiontermgroupname"))
                _tokens.Add(new SiteCollectionTermGroupNameToken(web));

            // We can exit the method here, if we already loaded all term groups or the template does not contain at least
            // one "sitecollectiontermsetid" token. Otherwise, we want to explicitly load the site collection term group
            // and its term sets in order to support "termsetid" token referencing a site-specific term set.
            if (_loadSiteCollectionTermGroups && !siteCollectionTermSetIdTokenFound)
            {
                return;
            }

            // Load the site collection term group and its term sets.
            var site = ((ClientContext)web.Context).Site;
            var siteCollectionTermGroup = termStore.GetSiteCollectionGroup(site, true);
            web.Context.Load(siteCollectionTermGroup);

            try
            {
                web.Context.ExecuteQueryRetry();

                if (siteCollectionTermGroup.ServerObjectIsNull())
                {
                    return;
                }

                web.Context.Load(
                    siteCollectionTermGroup,
                    group => group.Name,
                    group => group.TermSets.Include(
                        ts => ts.Name,
                        ts => ts.Id));
                web.Context.ExecuteQueryRetry();

                foreach (var termSet in siteCollectionTermGroup.TermSets)
                {
                    // Add a normal "termsetid" token if not all term groups were loaded. There might be token which
                    // reference a site-specific term set by not using the "sitecollectiontermsetid" token.
                    if (!_loadSiteCollectionTermGroups)
                    {
                        _tokens.Add(new TermSetIdToken(web, siteCollectionTermGroup.Name, termSet.Name, termSet.Id));
                    }

                    // Add a "sitecollectiontermsetid" token if at least one of those were found.
                    if (siteCollectionTermSetIdTokenFound)
                    {
                        _tokens.Add(new SiteCollectionTermSetIdToken(web, termSet.Name, termSet.Id));
                    }
                }
            }
            catch (ServerUnauthorizedAccessException)
            {
                // If we don't have permission to access the TermGroup, just skip it
                Log.Warning(Constants.LOGGING_SOURCE, CoreResources.TermGroup_No_Access);
            }
            catch (NullReferenceException)
            {
                // If there isn't a default TermGroup for the Site Collection, we skip the terms in token handler
            }
        }

        private void AddAppPackagesTokens(Web web)
        {
            _tokens.RemoveAll(t => t.GetType() == typeof(AppPackageIdToken));

            var manager = new AppManager(web.Context as ClientContext);

            try
            {
                var appPackages = manager.GetAvailable();

                foreach (var app in appPackages)
                {
                    _tokens.Add(new AppPackageIdToken(web, app.Title, app.Id));
                }
            }
            catch (Exception)
            {
                // In case of any failure, just skip creating AppPackageIdToken instances
                // and move forward. It means that there is no AppCatalog or no ALM APIs
            }
        }

        private void AddStorageEntityTokens(Web web)
        {
            try
            {
                // First retrieve the entities from the app catalog
                var tenantEntities = new List<StorageEntity>();
                var siteEntities = new List<StorageEntity>();
                var appCatalogUri = web.GetAppCatalog();
                if (appCatalogUri != null)
                {
                    using (var clonedContext = web.Context.Clone(appCatalogUri))
                    {
                        var storageEntitiesIndex = clonedContext.Web.GetPropertyBagValueString("storageentitiesindex", "");
                        tenantEntities = ParseStorageEntitiesString(storageEntitiesIndex);
                    }
                }
                var appcatalog = (web.Context as ClientContext).Site.RootWeb.SiteCollectionAppCatalog;
                web.Context.Load(appcatalog);
                web.Context.ExecuteQueryRetry();
                if (appcatalog.ServerObjectIsNull == false)
                {
                    var storageEntitiesIndex = (web.Context as ClientContext).Site.RootWeb.GetPropertyBagValueString("storageentitiesindex", "");
                    siteEntities = ParseStorageEntitiesString(storageEntitiesIndex);
                }
                var combinedEntities = siteEntities.Concat(tenantEntities).GroupBy(x => x.Key).Select(x => x.First());

                foreach (var entity in combinedEntities)
                {
                    _tokens.Add(new StorageEntityValueToken(web, entity.Key, entity.Value));
                }
            }
            catch { }
        }

        private static List<StorageEntity> ParseStorageEntitiesString(string storageEntitiesIndex)
        {
            var storageEntitiesDict = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(storageEntitiesIndex);
            if (storageEntitiesDict == null)
            {
                return new List<StorageEntity>();
            }

            var storageEntities = new List<StorageEntity>(storageEntitiesDict.Count + 1);

            foreach (KeyValuePair<string, Dictionary<string, string>> pair in storageEntitiesDict)
            {
                var storageEntity = new StorageEntity
                {
                    Key = pair.Key,
                    Value = pair.Value["Value"],
                    Comment = pair.Value["Comment"],
                    Description = pair.Value["Description"]
                };

                storageEntities.Add(storageEntity);
            }

            return storageEntities;
        }

        private void AddSiteDesignTokens(Web web, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            try
            {
                using (var tenantContext = web.Context.Clone(web.GetTenantAdministrationUrl(), applyingInformation?.AccessTokens))
                {
                    var tenant = new Tenant(tenantContext);
                    var designs = tenant.GetSiteDesigns();

                    tenantContext.Load(designs);
                    tenantContext.ExecuteQueryRetry();

                    foreach (var design in designs)
                    {
                        _tokens.Add(new SiteDesignIdToken(web, design.Title, design.Id));
                    }
                }
            }
            catch
            {
            }
        }

        private void AddPageUniqueIdTokens(Web web)
        {
            var pagesList = web.GetListByUrl("SitePages", p => p.RootFolder);

            var query = new CamlQuery()
            {
                ViewXml = "<View><ViewFields><FieldRef Name='UniqueId'/><FieldRef Name='FileLeafRef' /></ViewFields></View><RowLimit Paging='TRUE'>100</RowLimit>"
            };

            do
            {
                var items = pagesList.GetItems(query);

                web.Context.Load(items);
                web.Context.ExecuteQueryRetry();

                foreach (var item in items)
                {
                    _tokens.Add(new PageUniqueIdToken(web, $"SitePages/{item["FileLeafRef"]}", Guid.Parse(item["UniqueId"].ToString())));
                    _tokens.Add(new PageUniqueIdEncodedToken(web, $"SitePages/{item["FileLeafRef"]}", Guid.Parse(item["UniqueId"].ToString())));
                }

                query.ListItemCollectionPosition = items.ListItemCollectionPosition;
            } while (query.ListItemCollectionPosition != null);
        }

        private void AddSiteScriptTokens(Web web, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            try
            {
                using (var tenantContext = web.Context.Clone(web.GetTenantAdministrationUrl(), applyingInformation?.AccessTokens))
                {
                    var tenant = new Tenant(tenantContext);
                    var scripts = tenant.GetSiteScripts();

                    tenantContext.Load(scripts);
                    tenantContext.ExecuteQueryRetry();

                    foreach (var script in scripts)
                    {
                        _tokens.Add(new SiteScriptIdToken(web, script.Title, script.Id));
                    }
                }
            }
            catch
            {
            }
        }

        private void AddContentTypeTokens(Web web)
        {
            _tokens.RemoveAll(t => t.GetType() == typeof(ContentTypeIdToken));

            web.Context.Load(web.AvailableContentTypes, cs => cs.Include(ct => ct.StringId, ct => ct.Name));
            web.Context.ExecuteQueryRetry();

            foreach (var ct in web.AvailableContentTypes)
            {
                _tokens.Add(new ContentTypeIdToken(web, ct.Name, ct.StringId));
            }
        }

        internal void RebuildListTokens(Web web)
        {
            RebuildListTokens(web, _addTokenWithCacheUpdateDelegate);
        }

        private void RebuildListTokens(Web web, Action<TokenDefinition> addToken)
        {
            web.EnsureProperties(w => w.ServerRelativeUrl, w => w.Language);

            Predicate<TokenDefinition> listTokenTypes = t => t.GetType() == typeof(ListIdToken)
                                                             || t.GetType() == typeof(ListUrlToken)
                                                             || t.GetType() == typeof(ListViewIdToken)
                                                             || t.GetType() == typeof(ListContentTypeIdToken);

            // Remove tokens from TokenDictionary and ListTokenDictionary
            foreach (var listToken in _tokens.FindAll(listTokenTypes))
            {
                foreach (string token in listToken.GetUnescapedTokens())
                {
                    _tokenDictionary.Remove(token);
                    if (listToken is ListIdToken)
                    {
                        _listTokenDictionary.Remove(token);
                    }
                }
            }

            _tokens.RemoveAll(listTokenTypes);

            web.Context.Load(web.Lists, ls => ls.Include(l => l.Id, l => l.Title, l => l.RootFolder.ServerRelativeUrl, l => l.Views, l => l.ContentTypes, l => l.TitleResource));
            web.Context.ExecuteQueryRetry();

            foreach (var list in web.Lists)
            {
                addToken(new ListIdToken(web, list.Title, list.Id));

                var mainLanguageName = GetListTitleForMainLanguage(web, list.Title);
                if (!string.IsNullOrWhiteSpace(mainLanguageName) && mainLanguageName != list.Title)
                {
                    addToken(new ListIdToken(web, mainLanguageName, list.Id));
                }

                addToken(new ListUrlToken(web, list.Title, list.RootFolder.ServerRelativeUrl.Substring(web.ServerRelativeUrl.TrimEnd(UrlSeparators).Length + 1)));

                foreach (var view in list.Views)
                {
                    addToken(new ListViewIdToken(web, list.Title, view.Title, view.Id));
                }

                foreach (var contentType in list.ContentTypes)
                {
                    addToken(new ListContentTypeIdToken(web, list.Title, contentType));
                }
            }

            if (!web.IsSubSite())
            {
                return;
            }

            // Add lists from rootweb
            var rootWeb = (web.Context as ClientContext).Site.RootWeb;
            rootWeb.EnsureProperty(w => w.ServerRelativeUrl);
            rootWeb.Context.Load(rootWeb.Lists, ls => ls.Include(l => l.Id, l => l.Title, l => l.RootFolder.ServerRelativeUrl, l => l.Views));
            rootWeb.Context.ExecuteQueryRetry();

            foreach (var rootList in rootWeb.Lists)
            {
                // token already there? Skip the list
                if (web.Lists.Any(l => l.Title == rootList.Title))
                {
                    continue;
                }

                addToken(new ListIdToken(web, rootList.Title, rootList.Id));
                addToken(new ListUrlToken(web, rootList.Title, rootList.RootFolder.ServerRelativeUrl.Substring(rootWeb.ServerRelativeUrl.TrimEnd(UrlSeparators).Length + 1)));

                foreach (var view in rootList.Views)
                {
                    addToken(new ListViewIdToken(rootWeb, rootList.Title, view.Title, view.Id));
                }
            }
        }

        /// <summary>
        /// Gets list of token resource values
        /// </summary>
        /// <param name="tokenValue">Token value</param>
        /// <returns>Returns list of token resource values</returns>
        public List<Tuple<string, string>> GetResourceTokenResourceValues(string tokenValue)
        {
            var resourceValues = new List<Tuple<string, string>>();
            tokenValue = $"{{{Regex.Escape(tokenValue.Trim(TokenBoundaryChars))}}}"; // since LocalizationToken are Regex.Escaped before load

            foreach (var token in _tokens.OfType<LocalizationToken>())
            {
                if (Array.IndexOf(token.GetTokens(), tokenValue) == -1)
                {
                    continue;
                }

                foreach (var entry in token.ResourceEntries)
                {
                    var ci = new CultureInfo(entry.LCID);
                    resourceValues.Add(new Tuple<string, string>(ci.Name, ParseString(entry.Value)));
                }
            }

            return resourceValues;
        }

        /// <summary>
        /// Parses the string
        /// </summary>
        /// <param name="input">input string to parse</param>
        /// <returns>Returns parsed string</returns>
        public string ParseString(string input)
        {
            return ParseString(input, null);
        }

        /// <summary>
        /// Gets left over tokens
        /// </summary>
        /// <param name="input">input string</param>
        /// <returns>Returns collections of left over tokens</returns>
        public IEnumerable<string> GetLeftOverTokens(string input)
        {
            var values = new List<string>();

            foreach (Match match in ReGuid.Matches(input))
            {
                string value = match.Value;
                if (!Guid.TryParse(value, out _))
                {
                    values.Add(value);
                }
            }

            return values;
        }

        /// <summary>
        /// Parses given string for a webpart making sure we only parse the token for a given web
        /// </summary>
        /// <param name="input">input string</param>
        /// <param name="web">filters the tokens on web id</param>
        /// <param name="tokensToSkip">array of tokens to skip</param>
        /// <returns>Returns parsed string for a webpart</returns>
        public string ParseStringWebPart(string input, Web web, params string[] tokensToSkip)
        {
            web.EnsureProperty(x => x.Id);

            if (string.IsNullOrEmpty(input) || input.IndexOfAny(TokenChars) == -1)
            {
                return input;
            }

            // Optimize for direct match with string search
            if (_tokenDictionary.TryGetValue(input, out string directMatch))
            {
                return directMatch;
            }

            // Support for non cached tokens
            if (TryGetNonCacheableToken(input, out string directMatchNonCached))
            {
                return directMatchNonCached;
            }

            string output = input;
            bool hasMatch = false;

            do
            {
                hasMatch = false;
                output = ReToken.Replace(output, match =>
                {
                    string tokenString = match.Groups[0].Value;

                    if (!_tokenDictionary.TryGetValue(tokenString, out string val))
                    {
                        return tokenString;
                    }

                    if (tokenString.IndexOf("listid", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        var token = _listTokenDictionary[tokenString];
                        if (!token.Web.Id.Equals(web.Id))
                        {
                            return tokenString;
                        }
                    }

                    hasMatch = true;
                    return val;
                });
            } while (hasMatch && input != output);

            return output;
        }

        private void BuildTokenCache(int cacheableCount, int nonCacheableCount)
        {
            _tokenDictionary = new Dictionary<string, string>(capacity: cacheableCount > 0 ? cacheableCount + 1 : 0, StringComparer.OrdinalIgnoreCase);
            _nonCacheableTokenDictionary = new Dictionary<string, TokenDefinition>(capacity: nonCacheableCount > 0 ? nonCacheableCount + 1 : 0, StringComparer.OrdinalIgnoreCase);
            _listTokenDictionary = new Dictionary<string, TokenDefinition>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < _tokens.Count; index++)
            {
                AddToTokenCache(_tokens[index]);
            }
        }

        private void AddToTokenCache(TokenDefinition definition)
        {
            IReadOnlyList<string> tokens = definition.GetUnescapedTokens();
            for (var index = 0; index < tokens.Count; index++)
            {
                string token = tokens[index];

                if (!definition.IsCacheable)
                {
                    _nonCacheableTokenDictionary[token] = definition;
                    continue;
                }

                string value = GetTokenReplacementValue(definition, token, _web.Context);
                _tokenDictionary[token] = value;

                if (definition is ListIdToken)
                {
                    _listTokenDictionary[token] = definition;
                }
            }
        }

        private bool TryGetNonCacheableToken(string input, out string value)
        {
            if (_nonCacheableTokenDictionary.TryGetValue(input, out TokenDefinition definition))
            {
                value = GetTokenReplacementValue(definition, input, _web.Context);
                return true;
            }

            value = null;
            return false;
        }

        private static string GetTokenReplacementValue(TokenDefinition definition, string tokenName, ClientRuntimeContext context)
        {
            int before = context.PendingRequestCount();
            string result = definition.GetReplaceValue();
            int after = context.PendingRequestCount();

            return before == after
                ? result
                : throw new Exception($"Token {tokenName} triggered an ExecuteQuery on the 'current' context. Please refactor this token to use the TokenContext class.");
        }

        /// <summary>
        /// Parses given string
        /// </summary>
        /// <param name="input">input string</param>
        /// <param name="tokensToSkip">array of tokens to skip</param>
        /// <returns>Returns parsed string</returns>
        public string ParseString(string input, params string[] tokensToSkip)
        {
            if (string.IsNullOrWhiteSpace(input) || input.IndexOfAny(TokenChars) == -1)
            {
                return input;
            }

            // Optimize for direct match with string search
            if (_tokenDictionary.TryGetValue(input, out string directMatch))
            {
                return directMatch;
            }

            // Support for non cached tokens
            if (TryGetNonCacheableToken(input, out string directMatchNonCached))
            {
                return directMatchNonCached;
            }

            string output = input;
            bool hasMatch = false;

            do
            {
                hasMatch = false;
                output = ReToken.Replace(output, match =>
                {
                    string tokenString = match.Groups[0].Value;

                    if (!_tokenDictionary.TryGetValue(tokenString, out string val))
                    {
                        return tokenString;
                    }

                    hasMatch = true;
                    return val;
                });
            } while (hasMatch && input != output);

            if (hasMatch)
            {
                return output;
            }

            var fallbackMatches = ReTokenFallback.Matches(output);
            if (fallbackMatches.Count == 0)
            {
                return output;
            }

            // If all token constructs {...} are GUID's, we can skip the expensive fallback
            bool needFallback = false;
            foreach (Match match in fallbackMatches)
            {
                if (!ReGuid.IsMatch(match.Value))
                {
                    needFallback = true;
                }
            }

            if (!needFallback)
            {
                return output;
            }

            // Fallback for tokens which may contain { or } as part of their name
            foreach (var pair in _tokenDictionary)
            {
                int idx = output.IndexOf(pair.Key, StringComparison.CurrentCultureIgnoreCase);
                if (idx != -1)
                {
                    output = output.Remove(idx, pair.Key.Length).Insert(idx, pair.Value);
                }

                if (!ReTokenFallback.IsMatch(output))
                {
                    break;
                }
            }

            return output;
        }

        public string ParseXmlStringWebpart(string inputXml, Web web, params string[] tokensToSkip)
        {
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(inputXml);

            // Swap out tokens in the attributes of all nodes.
            var nodes = xmlDoc.SelectNodes("//*");
            if (nodes != null)
            {
                foreach (var node in nodes.OfType<System.Xml.XmlElement>().Where(n => n.HasAttributes))
                {
                    foreach (var attribute in node.Attributes.OfType<System.Xml.XmlAttribute>().Where(a => !a.Name.Equals("xmlns", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Value)))
                    {
                        attribute.Value = ParseStringWebPart(attribute.Value, web, tokensToSkip);
                    }
                }
            }

            // Swap out tokens in the values of any elements with a text value.
            nodes = xmlDoc.SelectNodes("//*[text()]");
            if (nodes != null)
            {
                foreach (var node in nodes.OfType<System.Xml.XmlElement>())
                {
                    if (!string.IsNullOrEmpty(node.InnerText))
                    {
                        node.InnerText = ParseStringWebPart(node.InnerText, web, tokensToSkip);
                    }
                }
            }

            return xmlDoc.OuterXml;
        }

        public string ParseXmlString(string inputXml, params string[] tokensToSkip)
        {
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(inputXml);

            // Swap out tokens in the attributes of all nodes.
            var nodes = xmlDoc.SelectNodes("//*");
            if (nodes != null)
            {
                foreach (var node in nodes.OfType<System.Xml.XmlElement>().Where(n => n.HasAttributes))
                {
                    foreach (var attribute in node.Attributes.OfType<System.Xml.XmlAttribute>().Where(a => !a.Name.Equals("xmlns", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Value)))
                    {
                        attribute.Value = ParseString(attribute.Value, tokensToSkip);
                    }
                }
            }

            // Swap out tokens in the values of any elements with a text value.
            nodes = xmlDoc.SelectNodes("//*[text()]");
            if (nodes != null)
            {
                foreach (var node in nodes.OfType<System.Xml.XmlElement>())
                {
                    if (!string.IsNullOrEmpty(node.InnerText))
                    {
                        node.InnerText = ParseString(node.InnerText, tokensToSkip);
                    }
                }
            }

            return xmlDoc.OuterXml;
        }

        internal void RemoveToken<T>(T oldToken) where T : TokenDefinition
        {
            for (int i = 0; i < _tokens.Count; i++)
            {
                var tokenDefinition = _tokens[i];
                if (!tokenDefinition.GetTokens().SequenceEqual(oldToken.GetTokens()))
                {
                    continue;
                }

                _tokens.RemoveAt(i);

                foreach (string token in tokenDefinition.GetUnescapedTokens())
                {
                    _tokenDictionary.Remove(token);
                    _nonCacheableTokenDictionary.Remove(token);
                }

                break;
            }
        }

        /// <summary>
        /// This method retrieves the title of a list in the main language of the site
        /// </summary>
        /// <param name="web">The current Web</param>
        /// <param name="name">The title of the list in the current user's language</param>
        /// <returns>The title of the list in the main language of the site</returns>
        private string GetListTitleForMainLanguage(Web web, string name)
        {
            if (_listsTitles.TryGetValue(name, out string title))
            {
                return title;
            }

            // Get the default culture for the current web
            var ci = new CultureInfo((int)web.Language);

            // Reset the cache of lists titles
            _listsTitles.Clear();

            const int batchSize = 20;
            int listCount = web.Lists.Count;
            var titles = new Tuple<string, ClientResult<string>>[batchSize];

            // Add the new lists title using the main language of the site
            for (var listIndex = 0; listIndex < listCount;)
            {
                for (int step = 0; step < batchSize; step++)
                {
                    var list = web.Lists[listIndex++];
                    titles[step] = Tuple.Create(list.Title, list.TitleResource.GetValueForUICulture(ci.Name));

                    if (listIndex == listCount)
                    {
                        break;
                    }
                }

                web.Context.ExecuteQueryRetry();

                for (var index = 0; index < titles.Length; index++)
                {
                    Tuple<string, ClientResult<string>> tuple = titles[index];
                    if (tuple == null)
                    {
                        break;
                    }

                    if (!_listsTitles.ContainsKey(tuple.Item1))
                    {
                        _listsTitles.Add(tuple.Item1, tuple.Item2.Value);
                    }
                }

                Array.Clear(titles, 0, titles.Length);
            }

            // If now we have the list title ...
            return _listsTitles.TryGetValue(name, out title)
                ? title // Return the title, if any
                : null;
        }

        private static List<string> ParseTemplate(ProvisioningTemplate template)
        {
            var tokenIds = new List<string>();

            // Add parameter tokenid if parameters are specified
            if (template.Parameters != null && template.Parameters.Count > 0)
            {
                tokenIds.Add("parameter");
            }

            var xml = template.ToXML();

            if (xml.IndexOfAny(TokenChars) == -1) return tokenIds;

            bool hasMatch;
            string tempXml;

            do
            {
                hasMatch = false;
                tempXml = ReToken.Replace(xml, match =>
                {
                    for (int i = 0; i < match.Groups.Count; i++)
                    {
                        if (ReGuid.IsMatch(match.Groups[i].Value))
                        {
                            continue;
                        }

                        var originalTokenString = match.Groups[i].Value.Replace("{", "").Replace("}", "").ToLower();

                        var tokenStringToAdd = originalTokenString;
                        var colonIndex = tokenStringToAdd.IndexOf(":");
                        if (colonIndex > -1)
                        {
                            tokenStringToAdd = tokenStringToAdd.Substring(0, colonIndex);
                        }
                        if (!tokenIds.Contains(tokenStringToAdd) && !string.IsNullOrEmpty(tokenStringToAdd))
                        {
                            tokenIds.Add(tokenStringToAdd);
                        }

                        // If sequencesitetoken is used we need to make sure that the corresponding site token is also loaded
                        if (string.Equals(tokenStringToAdd, "sequencesitetoken", StringComparison.OrdinalIgnoreCase))
                        {
                            var sequenceSiteTokenArray = originalTokenString.Split(InternalTokenDelimiters);
                            if (sequenceSiteTokenArray.Length > 2 && !string.IsNullOrWhiteSpace(sequenceSiteTokenArray[2]) && !tokenIds.Contains(sequenceSiteTokenArray[2]))
                            {
                                tokenIds.Add(sequenceSiteTokenArray[2]);
                            }
                        }
                    }

                    return "-";
                });
            } while (hasMatch && xml != tempXml);

            return tokenIds;
        }

        private static void AddResourceEntry(string key, int lcid, string value, Dictionary<string, List<ResourceEntry>> dictionary)
        {
            if (!dictionary.TryGetValue(key, out List<ResourceEntry> entries))
            {
                entries = new List<ResourceEntry>();
                dictionary.Add(key, entries);
            }

            entries.Add(new ResourceEntry { LCID = lcid, Value = value });
        }

        private static void CalculateTokenCount(IReadOnlyList<TokenDefinition> tokens, out int cacheableCount, out int nonCacheableCount)
        {
            cacheableCount = 0;
            nonCacheableCount = 0;

            for (var index = 0; index < tokens.Count; index++)
            {
                TokenDefinition definition = tokens[index];

                if (definition.IsCacheable)
                {
                    cacheableCount += definition.TokenCount;
                }
                else
                {
                    nonCacheableCount += definition.TokenCount;
                }
            }
        }

        private void AddTokenWithCacheUpdate(TokenDefinition tokenDefinition)
        {
            _tokens.Add(tokenDefinition);
            AddToTokenCache(tokenDefinition);
        }

        private void AddTokenToList(TokenDefinition tokenDefinition)
        {
            _tokens.Add(tokenDefinition);
        }

        /// <summary>
        /// Clones the current TokenParser instance into a new instance
        /// </summary>
        /// <returns>New cloned instance of the TokenParser</returns>
        public object Clone()
        {
            return new TokenParser(_web, _tokens, _loadSiteCollectionTermGroups);
        }
    }
}
