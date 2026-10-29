using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Data.Sqlite;
using UHD.Sincronizacao;

namespace UHD.Servidor
{
    /// <summary>
    /// A lógica do servidor: recebe o que cada posto tem de novo, atribui os números de registo definitivos,
    /// junta doentes repetidos, garante uma só versão ativa por registo, e devolve ao posto o que é novo.
    /// </summary>
    public class ServicoSincronizacao
    {
        private const int LimiteDescarga = 500;
        private const string OrigemServidor = "servidor";

        private static readonly SemaphoreSlim Bloqueio = new SemaphoreSlim(1, 1);
        private static readonly Regex PadraoVersao = new Regex(@"^(?<raiz>.*)-(?<letra>[A-Z])$", RegexOptions.Compiled);

        private readonly BaseDados _bd;

        public ServicoSincronizacao(BaseDados bd)
        {
            _bd = bd;
        }

        public async Task<RespostaSincronizacao> ProcessarAsync(PedidoSincronizacao pedido)
        {
            // Um pedido de cada vez: garante números de registo sem repetições
            await Bloqueio.WaitAsync();
            try
            {
                return Processar(pedido);
            }
            finally
            {
                Bloqueio.Release();
            }
        }

        private RespostaSincronizacao Processar(PedidoSincronizacao pedido)
        {
            using var ligacao = _bd.Abrir();
            using var transacao = ligacao.BeginTransaction();

            var resposta = new RespostaSincronizacao();

            // Por esta ordem, para os doentes e utilizadores existirem antes das avaliações
            foreach (var alteracao in pedido.Alteracoes.OrderBy(a => Prioridade(a.Tabela)))
            {
                switch (alteracao.Tabela)
                {
                    case "Utilizadores": ProcessarUtilizador(ligacao, transacao, pedido.Posto, alteracao); break;
                    case "Doentes": ProcessarDoente(ligacao, transacao, pedido.Posto, alteracao, resposta); break;
                    case "Avaliacoes": ProcessarAvaliacao(ligacao, transacao, pedido.Posto, alteracao); break;
                    case "VersoesAvaliacao": ProcessarVersao(ligacao, transacao, pedido.Posto, alteracao); break;
                }
            }

            // O que é novo para este posto (ignora o que ele próprio enviou sem alterações)
            long ultima = UltimaSeq(ligacao, transacao);
            var novas = new List<AlteracaoDto>();

            using (var cmd = ligacao.CreateCommand())
            {
                cmd.Transaction = transacao;
                cmd.CommandText = @"
                    SELECT Tabela, Chave, Dados, Seq
                    FROM Entidades
                    WHERE Seq > $cursor AND Origem <> $posto
                    ORDER BY Seq
                    LIMIT $limite;";
                cmd.Parameters.AddWithValue("$cursor", pedido.Cursor);
                cmd.Parameters.AddWithValue("$posto", pedido.Posto);
                cmd.Parameters.AddWithValue("$limite", LimiteDescarga + 1);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    novas.Add(new AlteracaoDto
                    {
                        Tabela = reader.GetString(0),
                        Chave = reader.GetString(1),
                        Dados = (JsonObject)JsonNode.Parse(reader.GetString(2))!,
                        Seq = reader.GetInt64(3)
                    });
                }
            }

            resposta.TemMais = novas.Count > LimiteDescarga;
            if (resposta.TemMais)
                novas = novas.Take(LimiteDescarga).ToList();

            resposta.Alteracoes = novas;
            resposta.Cursor = resposta.TemMais ? novas[^1].Seq : ultima;

            transacao.Commit();

            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  posto {Resumo(pedido.Posto)}: recebeu {pedido.Alteracoes.Count}, devolveu {novas.Count}");
            return resposta;
        }

        private static int Prioridade(string tabela) => tabela switch
        {
            "Utilizadores" => 0,
            "Doentes" => 1,
            "Avaliacoes" => 2,
            _ => 3
        };

        private static string Resumo(string posto) => posto.Length > 8 ? posto[..8] : posto;

        // =========================================================
        // UTILIZADORES (a chave é o nº de funcionário)
        // =========================================================

        private void ProcessarUtilizador(SqliteConnection lig, SqliteTransaction tx, string posto, AlteracaoDto a)
        {
            string numero = Texto(a.Dados, "NumeroFuncionario")?.Trim() ?? string.Empty;
            if (numero.Length == 0)
                return;

            Guardar(lig, tx, "Utilizadores", numero.ToLowerInvariant(), a.Dados, posto);
        }

        // =========================================================
        // DOENTES (a chave é o Uid; o mesmo nº de processo = o mesmo doente)
        // =========================================================

        private void ProcessarDoente(SqliteConnection lig, SqliteTransaction tx, string posto, AlteracaoDto a,
                                     RespostaSincronizacao resposta)
        {
            string uid = Texto(a.Dados, "Uid") ?? a.Chave;
            if (string.IsNullOrWhiteSpace(uid))
                return;

            // Já foi reconhecido como repetido de outro doente: ignora este
            string? alvo = ObterSubstituicao(lig, tx, "Doentes", uid);
            if (alvo != null)
            {
                AdicionarSubstituicao(lig, tx, resposta, uid, alvo);
                return;
            }

            string processo = (Texto(a.Dados, "NumeroProcesso") ?? string.Empty).Trim();

            if (processo.Length > 0)
            {
                using var cmd = lig.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    SELECT Chave FROM Entidades
                    WHERE Tabela = 'Doentes' AND Chave <> $uid
                      AND LOWER(TRIM(json_extract(Dados, '$.NumeroProcesso'))) = LOWER($proc)
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("$uid", uid);
                cmd.Parameters.AddWithValue("$proc", processo);

                string? existente = cmd.ExecuteScalar() as string;
                if (existente != null)
                {
                    using var ins = lig.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO Substituicoes (Tabela, De, Para) VALUES ('Doentes', $de, $para);";
                    ins.Parameters.AddWithValue("$de", uid);
                    ins.Parameters.AddWithValue("$para", existente);
                    ins.ExecuteNonQuery();

                    AdicionarSubstituicao(lig, tx, resposta, uid, existente);
                    return;
                }
            }

            Guardar(lig, tx, "Doentes", uid, a.Dados, posto);
        }

        private void AdicionarSubstituicao(SqliteConnection lig, SqliteTransaction tx, RespostaSincronizacao resposta,
                                           string de, string para)
        {
            if (resposta.Substituicoes.Any(s => s.De == de))
                return;

            resposta.Substituicoes.Add(new SubstituicaoDto
            {
                Tabela = "Doentes",
                De = de,
                Para = para,
                Dados = LerDados(lig, tx, "Doentes", para)
            });
        }

        // =========================================================
        // AVALIAÇÕES (número definitivo, letra da versão, versão ativa)
        // =========================================================

        private void ProcessarAvaliacao(SqliteConnection lig, SqliteTransaction tx, string posto, AlteracaoDto a)
        {
            var d = a.Dados;
            string chave = Texto(d, "IdAvaliacao") ?? a.Chave;
            if (string.IsNullOrWhiteSpace(chave))
                return;

            bool modificado = false;

            // 1) Doente repetido: aponta para o doente que ficou
            string? doenteUid = Texto(d, "DoenteUid");
            if (doenteUid != null)
            {
                string? alvo = ObterSubstituicao(lig, tx, "Doentes", doenteUid);
                if (alvo != null)
                {
                    d["DoenteUid"] = alvo;
                    modificado = true;
                }
            }

            // 2) Número de registo definitivo
            string numero = Texto(d, "NumeroRegisto") ?? string.Empty;
            string raiz = numero;
            string? letra = null;

            var m = PadraoVersao.Match(numero);
            if (m.Success)
            {
                raiz = m.Groups["raiz"].Value;
                letra = m.Groups["letra"].Value;
            }

            if (raiz.StartsWith("PROV-", StringComparison.Ordinal))
                raiz = ObterOuAtribuirDefinitivo(lig, tx, raiz, AnoDe(d));

            // 3) Duas versões da mesma letra (feitas em postos diferentes): escolhe a próxima letra livre
            if (letra != null)
            {
                for (int tentativa = 0; tentativa < 26 && ExisteOutroComNumero(lig, tx, chave, $"{raiz}-{letra}"); tentativa++)
                    letra = ((char)(letra[0] + 1)).ToString();
            }

            string final = letra == null ? raiz : $"{raiz}-{letra}";
            if (final != numero)
            {
                d["NumeroRegisto"] = final;
                modificado = true;
            }

            // 4) Só uma versão ativa por registo: a que chegou agora
            if (Booleano(d, "Atual"))
            {
                foreach (var (outraChave, outrosDados) in AvaliacoesDaCadeia(lig, tx, raiz, chave))
                {
                    if (!Booleano(outrosDados, "Atual"))
                        continue;

                    outrosDados["Atual"] = false;
                    Guardar(lig, tx, "Avaliacoes", outraChave, outrosDados, OrigemServidor);
                }
            }

            // Se o servidor mudou alguma coisa, o posto de origem também tem de a receber
            Guardar(lig, tx, "Avaliacoes", chave, d, modificado ? OrigemServidor : posto);
        }

        private string ObterOuAtribuirDefinitivo(SqliteConnection lig, SqliteTransaction tx, string provisorio, int ano)
        {
            // Reutiliza sempre o número anteriormente atribuído a este provisório.
            using (var cmd = lig.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT Definitivo FROM NumerosRegisto WHERE Provisorio = $p;";
                cmd.Parameters.AddWithValue("$p", provisorio);
                if (cmd.ExecuteScalar() is string existente)
                    return existente;
            }

            // Recupera o maior número já usado no ano, mesmo que o contador tenha ficado desatualizado.
            long maiorExistente = 0;
            using (var cmd = lig.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    SELECT Definitivo FROM NumerosRegisto
                    UNION ALL
                    SELECT json_extract(Dados, '$.NumeroRegisto')
                    FROM Entidades
                    WHERE Tabela = 'Avaliacoes';";
                using var reader = cmd.ExecuteReader();
                string prefixo = ano.ToString(CultureInfo.InvariantCulture) + "-";
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    string valor = reader.GetString(0);
                    if (!valor.StartsWith(prefixo, StringComparison.Ordinal)) continue;
                    string parte = valor.Substring(prefixo.Length);
                    if (parte.Length > 0 && parte.All(char.IsDigit) &&
                        long.TryParse(parte, NumberStyles.None, CultureInfo.InvariantCulture, out long n) &&
                        n > maiorExistente)
                        maiorExistente = n;
                }
            }

            long contadorAtual = 0;
            using (var cmd = lig.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT Ultimo FROM ContadoresRegisto WHERE Ano = $ano;";
                cmd.Parameters.AddWithValue("$ano", ano);
                object? valor = cmd.ExecuteScalar();
                if (valor != null && valor != DBNull.Value)
                    contadorAtual = Convert.ToInt64(valor, CultureInfo.InvariantCulture);
            }

            long proximo = Math.Max(contadorAtual, maiorExistente);
            string definitivo;
            do
            {
                proximo++;
                definitivo = $"{ano}-{proximo:D6}";
            }
            while (NumeroDefinitivoJaExiste(lig, tx, definitivo));

            using (var cmd = lig.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO ContadoresRegisto (Ano, Ultimo) VALUES ($ano, $ultimo)
                    ON CONFLICT(Ano) DO UPDATE SET Ultimo = excluded.Ultimo;";
                cmd.Parameters.AddWithValue("$ano", ano);
                cmd.Parameters.AddWithValue("$ultimo", proximo);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = lig.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO NumerosRegisto (Provisorio, Definitivo) VALUES ($p, $d);";
                cmd.Parameters.AddWithValue("$p", provisorio);
                cmd.Parameters.AddWithValue("$d", definitivo);
                cmd.ExecuteNonQuery();
            }

            return definitivo;
        }

        private static bool NumeroDefinitivoJaExiste(SqliteConnection lig, SqliteTransaction tx, string numero)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(*) FROM NumerosRegisto WHERE Definitivo = $numero) +
                    (SELECT COUNT(*) FROM Entidades
                     WHERE Tabela = 'Avaliacoes'
                       AND json_extract(Dados, '$.NumeroRegisto') = $numero);";
            cmd.Parameters.AddWithValue("$numero", numero);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private static int AnoDe(JsonObject d)
        {
            string? criacao = Texto(d, "DataHoraCriacao");
            if (criacao != null && criacao.Length >= 4 && int.TryParse(criacao[..4], out int ano) && ano > 2000)
                return ano;

            return DateTime.Now.Year;
        }

        private bool ExisteOutroComNumero(SqliteConnection lig, SqliteTransaction tx, string chave, string numero)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                SELECT COUNT(*) FROM Entidades
                WHERE Tabela = 'Avaliacoes' AND Chave <> $chave
                  AND json_extract(Dados, '$.NumeroRegisto') = $numero;";
            cmd.Parameters.AddWithValue("$chave", chave);
            cmd.Parameters.AddWithValue("$numero", numero);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private List<(string Chave, JsonObject Dados)> AvaliacoesDaCadeia(SqliteConnection lig, SqliteTransaction tx,
                                                                         string raiz, string excluirChave)
        {
            var lista = new List<(string, JsonObject)>();

            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                SELECT Chave, Dados FROM Entidades
                WHERE Tabela = 'Avaliacoes' AND Chave <> $chave
                  AND (json_extract(Dados, '$.NumeroRegisto') = $raiz
                       OR json_extract(Dados, '$.NumeroRegisto') LIKE $raiz || '-_');";
            cmd.Parameters.AddWithValue("$chave", excluirChave);
            cmd.Parameters.AddWithValue("$raiz", raiz);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                lista.Add((reader.GetString(0), (JsonObject)JsonNode.Parse(reader.GetString(1))!));

            return lista;
        }

        // =========================================================
        // VERSÕES (histórico de alterações)
        // =========================================================

        private void ProcessarVersao(SqliteConnection lig, SqliteTransaction tx, string posto, AlteracaoDto a)
        {
            string uid = Texto(a.Dados, "Uid") ?? a.Chave;
            if (string.IsNullOrWhiteSpace(uid))
                return;

            Guardar(lig, tx, "VersoesAvaliacao", uid, a.Dados, posto);
        }

        // =========================================================
        // ARMAZENAMENTO
        // =========================================================

        private void Guardar(SqliteConnection lig, SqliteTransaction tx, string tabela, string chave, JsonObject dados, string origem)
        {
            long seq = ProximaSeq(lig, tx);

            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Entidades (Tabela, Chave, Dados, Seq, Origem, AtualizadoEm)
                VALUES ($tabela, $chave, $dados, $seq, $origem, $data)
                ON CONFLICT(Tabela, Chave) DO UPDATE SET Dados = excluded.Dados, Seq = excluded.Seq,
                                                        Origem = excluded.Origem, AtualizadoEm = excluded.AtualizadoEm;";
            cmd.Parameters.AddWithValue("$tabela", tabela);
            cmd.Parameters.AddWithValue("$chave", chave);
            cmd.Parameters.AddWithValue("$dados", dados.ToJsonString());
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$origem", origem);
            cmd.Parameters.AddWithValue("$data", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }

        private static JsonObject? LerDados(SqliteConnection lig, SqliteTransaction tx, string tabela, string chave)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Dados FROM Entidades WHERE Tabela = $t AND Chave = $c;";
            cmd.Parameters.AddWithValue("$t", tabela);
            cmd.Parameters.AddWithValue("$c", chave);

            return cmd.ExecuteScalar() is string json ? (JsonObject)JsonNode.Parse(json)! : null;
        }

        private static string? ObterSubstituicao(SqliteConnection lig, SqliteTransaction tx, string tabela, string de)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Para FROM Substituicoes WHERE Tabela = $t AND De = $d;";
            cmd.Parameters.AddWithValue("$t", tabela);
            cmd.Parameters.AddWithValue("$d", de);
            return cmd.ExecuteScalar() as string;
        }

        private static long ProximaSeq(SqliteConnection lig, SqliteTransaction tx)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Sequencia SET Ultimo = Ultimo + 1 WHERE Id = 1; SELECT Ultimo FROM Sequencia WHERE Id = 1;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static long UltimaSeq(SqliteConnection lig, SqliteTransaction tx)
        {
            using var cmd = lig.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Ultimo FROM Sequencia WHERE Id = 1;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        // =========================================================
        // LEITURA DE CAMPOS JSON
        // =========================================================

        private static string? Texto(JsonObject o, string campo)
            => o[campo] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;

        private static bool Booleano(JsonObject o, string campo)
            => o[campo] is JsonValue v && v.TryGetValue<bool>(out bool b) && b;
    }
}
