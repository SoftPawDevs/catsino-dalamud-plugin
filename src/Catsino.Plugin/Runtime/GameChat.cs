using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Catsino.Plugin.Runtime;

public static unsafe class GameChat
{
    private const int MaximumMessageBytes = 500;

    public static string BuildTellCommand(string characterName, string homeWorld, Uri inviteUrl)
    {
        var identityError = Security.DealerInputValidator.ValidateCharacter(characterName, homeWorld);
        if (identityError is not null)
        {
            throw new ArgumentException(identityError);
        }

        if (!inviteUrl.IsAbsoluteUri ||
            !string.Equals(inviteUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            inviteUrl.OriginalString.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The invite URL must be an absolute HTTPS URL without whitespace.", nameof(inviteUrl));
        }

        var command = $"/tell {characterName}@{homeWorld} {inviteUrl.AbsoluteUri}";
        ValidateCommand(command);
        return command;
    }

    public static void SendCommand(string command)
    {
        ValidateCommand(command);
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            throw new InvalidOperationException("The FFXIV UI module is unavailable.");
        }

        var message = Utf8String.FromString(command);
        try
        {
            uiModule->ProcessChatBoxEntry(message);
        }
        finally
        {
            message->Dtor(true);
        }
    }

    private static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command[0] != '/')
        {
            throw new ArgumentException("A non-empty slash command is required.", nameof(command));
        }

        if (command.Any(char.IsControl))
        {
            throw new ArgumentException("The command contains control characters.", nameof(command));
        }

        if (Encoding.UTF8.GetByteCount(command) > MaximumMessageBytes)
        {
            throw new ArgumentException($"The command exceeds the {MaximumMessageBytes}-byte FFXIV chat limit.", nameof(command));
        }
    }
}
