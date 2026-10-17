using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Palabras.Auth;

internal sealed partial class ResilientKeyRepository : IXmlRepository
{
    private readonly FileSystemXmlRepository? _files;
    private readonly ILogger<ResilientKeyRepository> _logger;
    private readonly List<XElement> _memory = [];
    private readonly object _gate = new();
    private bool _unavailable;

    internal ResilientKeyRepository(string? path, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ResilientKeyRepository>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _files = new FileSystemXmlRepository(new DirectoryInfo(path), loggerFactory);
        }
        else
        {
            UsingProcessLocalKeys(_logger, null);
        }
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            if (_files != null && !_unavailable)
            {
                try
                {
                    var persisted = _files.GetAllElements();
                    _memory.Clear();
                    _memory.AddRange(persisted.Select(element => new XElement(element)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _unavailable = true;
                    UsingProcessLocalKeys(_logger, exception);
                }
            }
            return _memory.Select(element => new XElement(element)).ToArray();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_gate)
        {
            _memory.Add(new XElement(element));
            if (_files != null && !_unavailable)
            {
                try
                {
                    _files.StoreElement(element, friendlyName);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _unavailable = true;
                    UsingProcessLocalKeys(_logger, exception);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data protection persistence unavailable; cookies survive only this process. Configure a writable DataProtection:KeysPath.")]
    private static partial void UsingProcessLocalKeys(ILogger logger, Exception? exception);
}
