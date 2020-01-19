using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;
using KKManager.Updater.Data;
using KKManager.Updater.Windows;
using KKManager.Util;
using Newtonsoft.Json;

namespace KKManager.Updater.Sources
{
    public class MegaUpdater : UpdateSourceBase
    {
        private static readonly string _authPath = typeof(MegaUpdater).Assembly.Location + ".authInfos";
        private static readonly string _tokenPath = typeof(MegaUpdater).Assembly.Location + ".logonSessionToken";

        private readonly MegaApiClient _client;

        private List<INode> _allNodes;
        private static MegaApiClient.LogonSessionToken _loginToken;
        private static MegaApiClient.AuthInfos _authInfos;
        private static bool _anonymous;

        public MegaUpdater(Uri serverUri, NetworkCredential credentials) : base(serverUri.OriginalString, 10)
        {
            if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));
            if (serverUri.Host.ToLower() != "mega.nz")
                throw new NotSupportedException("The link doesn't point to mega.nz - " + serverUri);

            _client = new MegaApiClient();
            _currentFolderLink = serverUri;

            try
            {
                if (credentials != null)
                    _authInfos = _client.GenerateAuthInfos(credentials.UserName, credentials.Password);
                else if (File.Exists(_authPath))
                    _authInfos = JsonSerializer.CreateDefault().Deserialize<MegaApiClient.AuthInfos>(new JsonTextReader(File.OpenText(_authPath)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to create MegaApiClient.AuthInfos - " + ex);
            }

            try
            {
                if (File.Exists(_tokenPath))
                    _loginToken = JsonSerializer.CreateDefault().Deserialize<MegaApiClient.LogonSessionToken>(new JsonTextReader(File.OpenText(_tokenPath)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to read stored MegaApiClient.LogonSessionToken - " + ex);
            }
        }

        private readonly Uri _currentFolderLink;

        public override void Dispose()
        {
            try
            {
                _allNodes = null;
                if (_client != null && _client.IsLoggedIn)
                    _client.Logout();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override async Task<List<UpdateTask>> GetUpdateItems(CancellationToken cancellationToken)
        {
            await Connect(false);
            await RetryHelper.RetryOnExceptionAsync(async () => _allNodes = (await _client.GetNodesFromLinkAsync(_currentFolderLink)).ToList(), 2, TimeSpan.FromSeconds(1), cancellationToken);
            return await base.GetUpdateItems(cancellationToken);
        }

        protected override async Task<Stream> DownloadFileAsync(string updateFileName, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.DownloadAsync(GetNodeAtPath(updateFileName), new Progress<double>(), cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download file {updateFileName} - {ex}");
            }
            return null;
        }

        protected override IRemoteItem GetRemoteRootItem(string serverPath)
        {
            var updateNode = GetNodeAtPath(serverPath);
            return updateNode != null ? new MegaUpdateItem(updateNode, this, null) : null;
        }

        private INode GetNodeAtPath(string serverPath)
        {
            var root = _allNodes.Single(x => x.Type == NodeType.Root);
            // Find the remote directory
            var updateNode = root;
            var pathParts = serverPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pathPart in pathParts)
            {
                updateNode = GetSubNodes(updateNode).FirstOrDefault(node => node.Type == NodeType.Directory || node.Type == NodeType.File && string.Equals(node.Name, pathPart, StringComparison.OrdinalIgnoreCase));
                if (updateNode == null)
                    break;
            }
            return updateNode;
        }

        private async Task DownloadNodeAsync(MegaUpdateItem task, FileInfo downloadTarget, Progress<double> progress, CancellationToken cancellationToken)
        {
            await Connect(true);
            await _client.DownloadFileAsync(task.SourceItem, downloadTarget.FullName, progress, cancellationToken);
        }

        private async Task Connect(bool askToLogin)
        {
            await RetryHelper.RetryOnExceptionAsync(async () => await ConnectImpl(askToLogin), 2, TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        private async Task ConnectImpl(bool askToLogin)
        {
            if (_client.IsLoggedIn)
            {
                if (askToLogin && !_anonymous && _loginToken == null)
                {
                    await _client.LogoutAsync();
                    goto retryLoginWithAuth;
                }
                return;
            }

            if (_loginToken != null)
            {
                try
                {
                    await _client.LoginAsync(_loginToken);
                    if (_client.IsLoggedIn) return;
                }
                catch (Exception ex)
                {
                    _loginToken = null;
                    Console.WriteLine($"Failed to log in to mega with token, retrying full login - {ex}");
                }
            }

            retryLoginWithAuth:
            if (_authInfos != null)
            {
                _loginToken = await _client.LoginAsync(_authInfos);
                if (_client.IsLoggedIn) return;
            }

            if (_anonymous || !askToLogin)
            {
                await _client.LoginAnonymousAsync();
                if (_client.IsLoggedIn) return;
            }

            if (!askToLogin) return;

            var result = MegaLoginWindow.ShowDialog(_authInfos?.Email, _client);
            if (result == null)
                throw new OperationCanceledException();

            _authInfos = result.Item1;
            _loginToken = result.Item2;
            _anonymous = _loginToken == null;

            try
            {
                File.Delete(_tokenPath);
                if (_loginToken != null)
                    JsonSerializer.CreateDefault().Serialize(File.CreateText(_tokenPath), _loginToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save MegaApiClient.LogonSessionToken - " + ex);
            }
            try
            {
                File.Delete(_authPath);
                if (_authInfos != null)
                    JsonSerializer.CreateDefault().Serialize(File.CreateText(_authPath), _authInfos);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save new MegaApiClient.AuthInfos - " + ex);
            }

            if (!_client.IsLoggedIn)
                goto retryLoginWithAuth;
        }

        private IEnumerable<INode> GetSubNodes(INode rootNode)
        {
            return _allNodes.Where(x => x.ParentId == rootNode.Id);
        }

        public sealed class MegaUpdateItem : IRemoteItem
        {
            private readonly MegaUpdateItem _owner;
            private readonly MegaUpdater _source;
            public INode SourceItem { get; }

            public MegaUpdateItem(INode item, MegaUpdater source, MegaUpdateItem owner)
            {
                SourceItem = item ?? throw new ArgumentNullException(nameof(item));
                _source = source ?? throw new ArgumentNullException(nameof(source));
                ItemSize = item.Size;
                ModifiedTime = item.ModificationDate ?? item.CreationDate;
                Name = item.Name;
                IsDirectory = item.Type == NodeType.Directory;
                IsFile = item.Type == NodeType.File;

                if (owner != null)
                {
                    _owner = owner;

                    var current = this;
                    var result = string.Empty;
                    while (current != null)
                    {
                        result = current.Name + "/" + result;
                        current = current._owner;
                    }
                    ClientRelativeFileName = result.Trim('/');
                }
            }

            public string Name { get; }
            public long ItemSize { get; }
            public DateTime ModifiedTime { get; }
            public bool IsDirectory { get; }
            public bool IsFile { get; }
            public string ClientRelativeFileName { get; }

            public IRemoteItem[] GetDirectoryContents(CancellationToken cancellationToken)
            {
                return _source.GetSubNodes(SourceItem).Select(node => (IRemoteItem)new MegaUpdateItem(node, _source, this)).ToArray();
            }

            public Task Download(FileInfo downloadTarget, Progress<double> progressCallback, CancellationToken cancellationToken)
            {
                return _source.DownloadNodeAsync(this, downloadTarget, progressCallback, cancellationToken);
            }
        }
    }
}
