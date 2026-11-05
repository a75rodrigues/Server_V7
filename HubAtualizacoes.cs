using Microsoft.AspNetCore.SignalR;

namespace UHD.Servidor;

/// <summary>
/// Canal de notificações da UHD. Não transporta dados clínicos: apenas informa
/// que existem alterações centrais para os postos sincronizarem.
/// </summary>
public sealed class HubAtualizacoes : Hub
{
}
