using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.SignalR;
using System.Net.Sockets;
using System.Text;
using UHD.Servidor;
using UHD.Sincronizacao;

// =====================================================================
// SERVIDOR CENTRAL UHD  (fase 2: sincronização)
//
// Arrancar:      dotnet run
// Endereço:      appsettings.json -> "urls"
// Chave:         appsettings.json -> "ChaveAcesso" (a mesma da aplicação, em Administração > Servidor)
// Base de dados: appsettings.json -> "CaminhoBD" (por defeito Dados/uhd-servidor.db)
// Verificar:     abrir no browser  http://localhost:5080/
// =====================================================================

// Ativa o SQLite com cifra (SQLCipher) antes de qualquer ligação à base de dados
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// Autenticação integrada Windows (Kerberos/NTLM) para o registo automático dos postos.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddSignalR();

// Em desenvolvimento usa a pasta do projeto; publicado, usa a pasta do executável.
string raizServidor = File.Exists(Path.Combine(builder.Environment.ContentRootPath, "UHD.Servidor.csproj"))
    ? builder.Environment.ContentRootPath
    : AppContext.BaseDirectory;

string ResolverCaminho(string caminho) => Path.IsPathRooted(caminho)
    ? Path.GetFullPath(caminho)
    : Path.GetFullPath(Path.Combine(raizServidor, caminho));

string urls = builder.Configuration["urls"] ?? "http://0.0.0.0:5080";
builder.WebHost.UseUrls(urls);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

string chaveEsperada = app.Configuration["ChaveAcesso"] ?? string.Empty;
string caminhoBD = ResolverCaminho(app.Configuration["CaminhoBD"] ?? Path.Combine("Dados", "uhd-servidor.db"));
var bd = new BaseDados(caminhoBD, app.Configuration["ChaveBD"]);
var servico = new ServicoSincronizacao(bd);
string codigoEmparelhamento = Environment.GetEnvironmentVariable("UHD_PAIRING_CODE") ?? string.Empty;
bd.PrepararCodigoEmparelhamento(codigoEmparelhamento);

Console.WriteLine("==============================================");
Console.WriteLine(" SERVIDOR CENTRAL UHD");
Console.WriteLine($" À escuta em   : {urls}");
Console.WriteLine($" Base de dados : {bd.Caminho}");
Console.WriteLine($" Cifra da BD   : {(bd.Cifrada ? "ativa" : "DESATIVADA (defina ChaveBD em appsettings.json)")}");
Console.WriteLine($" Chave         : {(string.IsNullOrWhiteSpace(chaveEsperada) ? "NÃO DEFINIDA (appsettings.json não encontrado?)" : "definida (" + chaveEsperada.Length + " caracteres)")}");
Console.WriteLine(" Teste no browser: http://localhost:5080/");
Console.WriteLine("==============================================");

if (string.IsNullOrWhiteSpace(chaveEsperada) || chaveEsperada == "ALTERAR-ESTA-CHAVE")
{
    Console.WriteLine("ATENÇÃO: defina uma chave própria em appsettings.json (\"ChaveAcesso\") antes de usar em produção.");
}

// Regista cada pedido recebido
app.Use(async (contexto, seguinte) =>
{
    await seguinte();
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {contexto.Request.Method} {contexto.Request.Path}  ->  {contexto.Response.StatusCode}");
});

// Todos os pedidos a /api exigem a chave de acesso no cabeçalho "X-Chave-Acesso"
app.Use(async (contexto, seguinte) =>
{
    if (contexto.Request.Path.StartsWithSegments("/api") &&
        !contexto.Request.Path.StartsWithSegments("/api/postos/parear") &&
        !contexto.Request.Path.StartsWithSegments("/api/postos/registar-automatico"))
    {
        string tokenPosto = contexto.Request.Headers["X-UHD-Posto"].ToString();
        string chaveRecebida = contexto.Request.Headers["X-Chave-Acesso"].ToString();

        bool postoValido = false;
        if (!string.IsNullOrWhiteSpace(tokenPosto))
        {
            try { postoValido = bd.ValidarCredencialPosto(tokenPosto); }
            catch (Exception ex) { Console.WriteLine("Erro ao validar posto: " + ex.Message); }
        }

        bool chaveLegadaValida =
            !string.IsNullOrEmpty(chaveEsperada) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(chaveRecebida),
                Encoding.UTF8.GetBytes(chaveEsperada));

        if (!postoValido && !chaveLegadaValida)
        {
            contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await contexto.Response.WriteAsync("Credencial do posto inválida ou não autorizada.");
            return;
        }
    }

    await seguinte();
});

// Distribuição de versões do UHD (configurar DiretorioDistribuicao no servidor).
// Esta rota não usa a chave da API: limitar o acesso à rede interna através da firewall/reverse proxy.
string diretorioDistribuicao = ResolverCaminho(
    app.Configuration["DiretorioDistribuicao"] ??
    Environment.GetEnvironmentVariable("DISTRIBUICAO_DIRETORIO") ??
    "Distribuicao");

app.MapGet("/releases/{ficheiro}", IResult (string ficheiro) =>
{
    // Aceita apenas nomes de ficheiro simples; impede caminhos relativos ou acesso a outras pastas.
    if (ficheiro != Path.GetFileName(ficheiro) ||
        !(ficheiro.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
          (ficheiro.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
           ficheiro.Length <= 120)))
        return Results.BadRequest("Nome de ficheiro de distribuição inválido.");

    string caminho = Path.GetFullPath(Path.Combine(diretorioDistribuicao, ficheiro));
    if (!caminho.StartsWith(diretorioDistribuicao + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Caminho inválido.");
    if (!File.Exists(caminho))
        return Results.NotFound("Ficheiro de distribuição não encontrado.");

    string tipo = ficheiro.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
        ? "application/json"
        : "application/zip";
    return Results.File(caminho, tipo, enableRangeProcessing: true);
});

app.MapGet("/", () => "Servidor UHD ativo.");

// Canal de notificações em tempo real. A autenticação é validada pelo middleware /api.
app.MapHub<HubAtualizacoes>("/api/hubs/atualizacoes");

// Registo automático do posto com a identidade Windows autenticada.
app.MapPost("/api/postos/registar-automatico", (PedidoRegistoAutomatico pedido, HttpContext contexto) =>
{
    string identidade = contexto.User.Identity?.Name ?? string.Empty;
    if (string.IsNullOrWhiteSpace(identidade))
        return Results.Unauthorized();

    string nomeComputador = (pedido.NomeComputador ?? string.Empty).Trim();
    if (nomeComputador.Length == 0 || nomeComputador.Length > 100)
        return Results.BadRequest("Nome do computador inválido.");

    var criado = bd.RegistarPostoWindows(identidade, nomeComputador, pedido.CredencialAtual);
    return Results.Ok(new { postoId = criado.Id, credencial = criado.Token, identidadeWindows = identidade });
}).RequireAuthorization();

// Emparelhamento inicial legado: mantido temporariamente para compatibilidade.
app.MapPost("/api/postos/parear", (PedidoEmparelhamento pedido) =>
{
    if (string.IsNullOrWhiteSpace(codigoEmparelhamento))
        return Results.Problem("O emparelhamento não está configurado no servidor.", statusCode: 503);

    var criado = bd.EmparelharPosto(pedido.Codigo ?? string.Empty, pedido.Nome ?? string.Empty);
    if (criado == null) return Results.Unauthorized();

    // O token é devolvido uma única vez. A BD guarda apenas o respetivo hash.
    return Results.Ok(new { postoId = criado.Value.Id, credencial = criado.Value.Token });
});

app.MapGet("/api/ping", () => Results.Ok(new
{
    servidor = "UHD",
    versao = "2.0-sincronizacao",
    hora = DateTime.Now
}));

// Login central: permite autenticar um funcionário que ainda não existe na BD local.
app.MapPost("/api/login", (PedidoLogin pedido) =>
{
    string numero = (pedido.NumeroFuncionario ?? string.Empty).Trim();
    string password = pedido.Password ?? string.Empty;
    if (numero.Length == 0 || password.Length == 0)
        return Results.BadRequest(new { mensagem = "Indique o número de funcionário e a palavra-passe." });

    using var ligacao = bd.Abrir();
    using var cmd = ligacao.CreateCommand();
    cmd.CommandText = @"SELECT Dados FROM Entidades
                        WHERE Tabela = 'Utilizadores' AND LOWER(Chave) = LOWER($numero)
                        LIMIT 1;";
    cmd.Parameters.AddWithValue("$numero", numero);
    string? json = cmd.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(json)) return Results.Unauthorized();

    using var documento = System.Text.Json.JsonDocument.Parse(json);
    var raiz = documento.RootElement;
    string Campo(string nome) =>
        raiz.TryGetProperty(nome, out var valor) && valor.ValueKind == System.Text.Json.JsonValueKind.String
            ? valor.GetString() ?? string.Empty : string.Empty;
    bool Bool(string nome, bool padrao = false)
    {
        if (!raiz.TryGetProperty(nome, out var valor)) return padrao;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.Number && valor.TryGetInt32(out int n)) return n != 0;
        return padrao;
    }

    string hash = Campo("PasswordHash");
    if (hash.Length == 0 || !VerificarPasswordCentral(password, hash)) return Results.Unauthorized();
    if (!Bool("Ativo", true) || Bool("Bloqueado"))
        return Results.Json(new { mensagem = "Esta conta encontra-se inativa ou bloqueada." },
            statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(new
    {
        numeroFuncionario = Campo("NumeroFuncionario"),
        nome = Campo("Nome"),
        perfil = Campo("Perfil"),
        permissoes = Campo("Permissoes"),
        primeiroAcesso = Bool("PrimeiroAcesso")
    });
});


// Consulta do nome/estado do funcionário no servidor, protegida pela credencial do posto.
// Não devolve hashes nem outros dados de autenticação.
app.MapPost("/api/utilizadores/consultar", (PedidoConsultaUtilizador pedido) =>
{
    string numero = (pedido.NumeroFuncionario ?? string.Empty).Trim();
    if (numero.Length == 0)
        return Results.BadRequest(new { mensagem = "Indique o número de funcionário." });

    using var ligacao = bd.Abrir();
    using var cmd = ligacao.CreateCommand();
    cmd.CommandText = @"SELECT Dados FROM Entidades
                        WHERE Tabela = 'Utilizadores' AND LOWER(Chave) = LOWER($numero)
                        LIMIT 1;";
    cmd.Parameters.AddWithValue("$numero", numero);
    string? json = cmd.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(json))
        return Results.NotFound(new { mensagem = "O número de funcionário não existe na base de dados central." });

    using var documento = System.Text.Json.JsonDocument.Parse(json);
    var raiz = documento.RootElement;
    string Campo(string nome) =>
        raiz.TryGetProperty(nome, out var valor) && valor.ValueKind == System.Text.Json.JsonValueKind.String
            ? valor.GetString() ?? string.Empty : string.Empty;
    bool Bool(string nome, bool padrao = false)
    {
        if (!raiz.TryGetProperty(nome, out var valor)) return padrao;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        if (valor.ValueKind == System.Text.Json.JsonValueKind.Number && valor.TryGetInt32(out int n)) return n != 0;
        return padrao;
    }

    return Results.Ok(new
    {
        numeroFuncionario = Campo("NumeroFuncionario"),
        nome = Campo("Nome"),
        ativo = Bool("Ativo", true),
        bloqueado = Bool("Bloqueado")
    });
});


// Consulta de avaliações em tempo real na BD central.
// A autenticação do posto é aplicada pelo middleware /api antes de chegar a esta rota.
app.MapPost("/api/consulta/avaliacoes", (PedidoConsultaAvaliacoes pedido) =>
{
    string processo = (pedido.Processo ?? string.Empty).Trim();
    string nome = (pedido.Nome ?? string.Empty).Trim();
    string? data = pedido.Data?.ToString("yyyy-MM-dd");

    using var ligacao = bd.Abrir();
    using var cmd = ligacao.CreateCommand();
    cmd.CommandText = @"
        SELECT
            json_extract(a.Dados, '$.IdAvaliacao') AS IdAvaliacao,
            json_extract(a.Dados, '$.DataAvaliacao') AS DataAvaliacao,
            COALESCE(json_extract(a.Dados, '$.NumeroProcesso'), '') AS NumeroProcesso,
            COALESCE(json_extract(d.Dados, '$.Nome'), '') AS NomeDoente,
            COALESCE(json_extract(a.Dados, '$.NumeroEpisodio'), '') AS NumeroEpisodio,
            COALESCE(json_extract(a.Dados, '$.Servico'), '') AS Servico,
            COALESCE(json_extract(a.Dados, '$.Cama'), '') AS Cama,
            COALESCE(json_extract(a.Dados, '$.Idade'), 0) AS Idade,
            COALESCE(json_extract(a.Dados, '$.Diagnostico'), '') AS Diagnostico,
            COALESCE(json_extract(u.Dados, '$.Nome'), json_extract(a.Dados, '$.ProfissionalAvaliacao'), '') AS ProfissionalAvaliacao,
            COALESCE(json_extract(a.Dados, '$.Estado'), '') AS Estado,
            COALESCE(json_extract(a.Dados, '$.Atual'), 1) AS Atual,
            COALESCE(json_extract(a.Dados, '$.NumeroRegisto'), '') AS NumeroRegisto,
            COALESCE(json_extract(a.Dados, '$.Anulado'), 0) AS Anulado,
            COALESCE(json_extract(a.Dados, '$.MotivoAnulacao'), '') AS MotivoAnulacao,
            COALESCE(json_extract(a.Dados, '$.AnuladoPorNumero'), '') AS AnuladoPorNumero,
            COALESCE(json_extract(a.Dados, '$.DataHoraAnulacao'), '') AS DataHoraAnulacao
        FROM Entidades a
        LEFT JOIN Entidades d
            ON d.Tabela = 'Doentes'
           AND json_extract(d.Dados, '$.Uid') = json_extract(a.Dados, '$.DoenteUid')
        LEFT JOIN Entidades u
            ON u.Tabela = 'Utilizadores'
           AND LOWER(json_extract(u.Dados, '$.NumeroFuncionario')) =
               LOWER(json_extract(a.Dados, '$.ProfissionalNumero'))
        WHERE a.Tabela = 'Avaliacoes'
          AND ($processo = '' OR COALESCE(json_extract(a.Dados, '$.NumeroProcesso'), '') LIKE $processoLike)
          AND ($nome = '' OR COALESCE(json_extract(d.Dados, '$.Nome'), '') LIKE $nomeLike)
          AND ($data IS NULL OR DATE(json_extract(a.Dados, '$.DataAvaliacao')) = DATE($data))
        ORDER BY json_extract(a.Dados, '$.NumeroRegisto') DESC,
                 json_extract(a.Dados, '$.DataHoraCriacao') DESC
        LIMIT 10000;";
    cmd.Parameters.AddWithValue("$processo", processo);
    cmd.Parameters.AddWithValue("$processoLike", "%" + processo + "%");
    cmd.Parameters.AddWithValue("$nome", nome);
    cmd.Parameters.AddWithValue("$nomeLike", "%" + nome + "%");
    cmd.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);

    var resultados = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        string texto(int indice) => reader.IsDBNull(indice)
            ? string.Empty
            : Convert.ToString(reader.GetValue(indice), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        DateTime.TryParse(texto(1), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out DateTime dataAvaliacao);

        decimal.TryParse(texto(7), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out decimal idade);

        Guid.TryParse(texto(0), out Guid idAvaliacao);

        resultados.Add(new
        {
            idAvaliacao,
            dataAvaliacao,
            numeroProcesso = texto(2),
            nomeDoente = texto(3),
            numeroEpisodio = texto(4),
            servico = texto(5),
            cama = texto(6),
            idade,
            diagnostico = texto(8),
            profissionalAvaliacao = texto(9),
            estado = texto(10),
            atual = !reader.IsDBNull(11) && Convert.ToInt32(reader.GetValue(11)) != 0,
            numeroRegisto = texto(12),
            anulado = !reader.IsDBNull(13) && Convert.ToInt32(reader.GetValue(13)) != 0,
            motivoAnulacao = texto(14),
            anuladoPorNumero = texto(15),
            dataHoraAnulacao = DateTime.TryParse(texto(16), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out DateTime dataAnulacao) ? dataAnulacao : (DateTime?)null
        });
    }

    return Results.Ok(resultados);
});


// Devolve o documento central completo de uma avaliação e as entidades relacionadas.
// A aplicação usa-o para preencher a cache local apenas quando o profissional abre o registo.
app.MapGet("/api/consulta/avaliacoes/{id:guid}", (Guid id) =>
{
    using var ligacao = bd.Abrir();

    string? LerEntidade(string tabela, string campo, string valor)
    {
        using var cmd = ligacao.CreateCommand();
        cmd.CommandText = $"SELECT Dados FROM Entidades WHERE Tabela = $tabela AND json_extract(Dados, '$.{campo}') = $valor LIMIT 1;";
        cmd.Parameters.AddWithValue("$tabela", tabela);
        cmd.Parameters.AddWithValue("$valor", valor);
        return cmd.ExecuteScalar() as string;
    }

    using var cmdAvaliacao = ligacao.CreateCommand();
    cmdAvaliacao.CommandText = "SELECT Dados FROM Entidades WHERE Tabela = 'Avaliacoes' AND Chave = $id LIMIT 1;";
    cmdAvaliacao.Parameters.AddWithValue("$id", id.ToString());
    string? jsonAvaliacao = cmdAvaliacao.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(jsonAvaliacao))
        return Results.NotFound(new { mensagem = "A avaliação não existe na base de dados central." });

    var avaliacao = System.Text.Json.Nodes.JsonNode.Parse(jsonAvaliacao);
    string Campo(System.Text.Json.Nodes.JsonNode? no, string nome)
        => no is System.Text.Json.Nodes.JsonObject obj && obj[nome] != null
            ? obj[nome]!.ToString()
            : string.Empty;

    string doenteUid = Campo(avaliacao, "DoenteUid");
    System.Text.Json.Nodes.JsonNode? doente = null;
    if (!string.IsNullOrWhiteSpace(doenteUid))
    {
        string? jsonDoente = LerEntidade("Doentes", "Uid", doenteUid);
        if (!string.IsNullOrWhiteSpace(jsonDoente))
            doente = System.Text.Json.Nodes.JsonNode.Parse(jsonDoente);
    }

    var versoes = new System.Text.Json.Nodes.JsonArray();
    var numerosUtilizadores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string numeroCriador = Campo(avaliacao, "RegistadoPorNumero");
    string numeroProfissional = Campo(avaliacao, "ProfissionalNumero");
    if (!string.IsNullOrWhiteSpace(numeroCriador)) numerosUtilizadores.Add(numeroCriador);
    if (!string.IsNullOrWhiteSpace(numeroProfissional)) numerosUtilizadores.Add(numeroProfissional);

    using (var cmdVersoes = ligacao.CreateCommand())
    {
        cmdVersoes.CommandText = @"
            SELECT Dados FROM Entidades
            WHERE Tabela = 'VersoesAvaliacao'
              AND json_extract(Dados, '$.IdAvaliacao') = $id
            ORDER BY json_extract(Dados, '$.NumeroVersao');";
        cmdVersoes.Parameters.AddWithValue("$id", id.ToString());
        using var reader = cmdVersoes.ExecuteReader();
        while (reader.Read())
        {
            string json = reader.GetString(0);
            var versao = System.Text.Json.Nodes.JsonNode.Parse(json);
            string numero = Campo(versao, "UtilizadorNumero");
            if (!string.IsNullOrWhiteSpace(numero)) numerosUtilizadores.Add(numero);
            versoes.Add(versao);
        }
    }

    var utilizadores = new System.Text.Json.Nodes.JsonArray();
    foreach (string numero in numerosUtilizadores)
    {
        string? jsonUtilizador = LerEntidade("Utilizadores", "NumeroFuncionario", numero);
        if (!string.IsNullOrWhiteSpace(jsonUtilizador))
            utilizadores.Add(System.Text.Json.Nodes.JsonNode.Parse(jsonUtilizador));
    }

    return Results.Ok(new { avaliacao, doente, utilizadores, versoes });
});


// Procura um doente pelo número de processo diretamente na BD central.
app.MapPost("/api/consulta/doentes/por-processo", (PedidoConsultaDoente pedido) =>
{
    string processo = (pedido.NumeroProcesso ?? string.Empty).Trim();
    if (processo.Length == 0)
        return Results.BadRequest(new { mensagem = "Indique o número de processo." });

    using var ligacao = bd.Abrir();
    using var cmd = ligacao.CreateCommand();
    cmd.CommandText = @"
        SELECT Dados FROM Entidades
        WHERE Tabela = 'Doentes'
          AND LOWER(TRIM(COALESCE(json_extract(Dados, '$.NumeroProcesso'), ''))) = LOWER($processo)
        LIMIT 1;";
    cmd.Parameters.AddWithValue("$processo", processo);

    string? json = cmd.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(json))
        return Results.NotFound(new { mensagem = "Não existe doente com este número de processo na BD central." });

    return Results.Ok(System.Text.Json.Nodes.JsonNode.Parse(json));
});

static bool VerificarPasswordCentral(string password, string hashGuardado)
{
    try
    {
        if (hashGuardado.StartsWith("PBKDF2$", StringComparison.Ordinal))
        {
            string[] partes = hashGuardado.Split('$');
            if (partes.Length != 5 || !int.TryParse(partes[2], out int iteracoes) ||
                iteracoes < 1 || iteracoes > 5_000_000) return false;
            HashAlgorithmName algoritmo = partes[1] switch
            {
                "SHA256" => HashAlgorithmName.SHA256,
                "SHA512" => HashAlgorithmName.SHA512,
                _ => default
            };
            if (algoritmo == default) return false;
            byte[] salt = Convert.FromBase64String(partes[3]);
            byte[] original = Convert.FromBase64String(partes[4]);
            byte[] teste = Rfc2898DeriveBytes.Pbkdf2(password, salt, iteracoes, algoritmo, original.Length);
            return CryptographicOperations.FixedTimeEquals(teste, original);
        }

        string[] antigo = hashGuardado.Split(':');
        if (antigo.Length != 2) return false;
        byte[] saltAntigo = Convert.FromBase64String(antigo[0]);
        byte[] hashAntigo = Convert.FromBase64String(antigo[1]);
        byte[] testeAntigo = Rfc2898DeriveBytes.Pbkdf2(password, saltAntigo, 100_000, HashAlgorithmName.SHA256, hashAntigo.Length);
        return CryptographicOperations.FixedTimeEquals(testeAntigo, hashAntigo);
    }
    catch (FormatException) { return false; }
}

// Sincronização: o posto envia o que tem de novo e recebe o que mudou nos outros
app.MapPost("/api/sync", async (PedidoSincronizacao pedido, IHubContext<HubAtualizacoes> hub) =>
{
    try
    {
        var resposta = await servico.ProcessarAsync(pedido);

        // ProcessarAsync só devolve depois do COMMIT. A notificação nunca anuncia
        // alterações que ainda não estejam gravadas na base de dados central.
        if (pedido.Alteracoes.Count > 0)
            await hub.Clients.All.SendAsync("AlteracoesDisponiveis", pedido.Posto);

        return Results.Ok(resposta);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERRO na sincronização: {ex}");
        return Results.Problem(ex.Message);
    }
});

// Descoberta automática do servidor UHD na rede local
_ = Task.Run(async () =>
{
    try
    {
        using var udp = new UdpClient(5081);
        Console.WriteLine("Descoberta UHD ativa na porta UDP 5081.");

        while (true)
        {
            var pedido = await udp.ReceiveAsync();
            string mensagem = Encoding.UTF8.GetString(pedido.Buffer);

            if (mensagem != "UHD_DISCOVER_V1")
                continue;

            byte[] resposta = Encoding.UTF8.GetBytes("UHD_SERVER_V1|5080");
            await udp.SendAsync(resposta, resposta.Length, pedido.RemoteEndPoint);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Erro na descoberta UHD: " + ex.Message);
    }
});
app.Run();

public sealed record PedidoConsultaUtilizador(string? NumeroFuncionario);
public sealed record PedidoConsultaAvaliacoes(string? Processo, string? Nome, DateTime? Data);
public sealed record PedidoConsultaDoente(string? NumeroProcesso);

public sealed record PedidoLogin(string? NumeroFuncionario, string? Password);


public sealed record PedidoEmparelhamento(string? Codigo, string? Nome);

public sealed record PedidoRegistoAutomatico(string? NomeComputador, string? CredencialAtual);
