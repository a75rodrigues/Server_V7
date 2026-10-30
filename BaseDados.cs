using Microsoft.Data.Sqlite;

namespace UHD.Servidor
{
    /// <summary>
    /// Base de dados central. Guarda cada registo como um documento (JSON) na tabela Entidades,
    /// e tem as tabelas de apoio para a numeração dos registos e para juntar doentes repetidos.
    /// Por agora é um ficheiro SQLite no próprio servidor; mais tarde pode passar para SQL Server ou PostgreSQL.
    /// </summary>
    public class BaseDados
    {
        private readonly string _ligacao;

        /// <param name="chave">Chave de cifra do ficheiro (appsettings.json -> "ChaveBD"). Vazia = sem cifra.</param>
        public BaseDados(string caminho, string? chave)
        {
            string completo = Path.GetFullPath(caminho);
            Directory.CreateDirectory(Path.GetDirectoryName(completo)!);

            _ligacao = string.IsNullOrWhiteSpace(chave)
                ? $"Data Source={completo}"
                : $"Data Source={completo};Password={chave}";

            Cifrada = !string.IsNullOrWhiteSpace(chave);
            Caminho = completo;

            Inicializar();
        }

        public string Caminho { get; }

        public bool Cifrada { get; }

        public SqliteConnection Abrir()
        {
            var ligacao = new SqliteConnection(_ligacao);
            ligacao.Open();
            return ligacao;
        }

        private void Inicializar()
        {
            using var ligacao = Abrir();
            using var cmd = ligacao.CreateCommand();

            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;

                -- Todos os registos (utilizadores, doentes, avaliações, versões) como documentos.
                -- Seq cresce a cada alteração: é o que permite a cada posto pedir só o que é novo.
                CREATE TABLE IF NOT EXISTS Entidades (
                    Tabela TEXT NOT NULL,
                    Chave TEXT NOT NULL,
                    Dados TEXT NOT NULL,
                    Seq INTEGER NOT NULL,
                    Origem TEXT NOT NULL,
                    AtualizadoEm TEXT NOT NULL,
                    PRIMARY KEY (Tabela, Chave)
                );
                CREATE INDEX IF NOT EXISTS ix_entidades_seq ON Entidades(Seq);

                CREATE TABLE IF NOT EXISTS Sequencia (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Ultimo INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO Sequencia (Id, Ultimo) VALUES (1, 0);

                -- Número provisório (criado sem ligação) -> número definitivo
                CREATE TABLE IF NOT EXISTS NumerosRegisto (
                    Provisorio TEXT PRIMARY KEY,
                    Definitivo TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ContadoresRegisto (
                    Ano INTEGER PRIMARY KEY,
                    Ultimo INTEGER NOT NULL
                );

                -- Impede a atribuição do mesmo número definitivo a dois provisórios.
                CREATE UNIQUE INDEX IF NOT EXISTS ux_numerosregisto_definitivo
                    ON NumerosRegisto(Definitivo);

                -- Doentes repetidos: o identificador 'De' passa a ser o 'Para'
                CREATE TABLE IF NOT EXISTS Substituicoes (
                    Tabela TEXT NOT NULL,
                    De TEXT NOT NULL,
                    Para TEXT NOT NULL,
                    PRIMARY KEY (Tabela, De)
                );";

            cmd.CommandText += @"
                CREATE TABLE IF NOT EXISTS Postos (
                    Id TEXT PRIMARY KEY,
                    Nome TEXT NOT NULL,
                    TokenHash TEXT NOT NULL UNIQUE,
                    Ativo INTEGER NOT NULL DEFAULT 1,
                    CriadoEm TEXT NOT NULL,
                    UltimoAcesso TEXT
                );
                CREATE TABLE IF NOT EXISTS CodigoEmparelhamento (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    CodigoHash TEXT NOT NULL,
                    Consumido INTEGER NOT NULL DEFAULT 0,
                    CriadoEm TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS PostosWindows (
                    IdentidadeWindows TEXT NOT NULL,
                    NomeComputador TEXT NOT NULL,
                    PostoId TEXT NOT NULL UNIQUE,
                    CriadoEm TEXT NOT NULL,
                    PRIMARY KEY (IdentidadeWindows, NomeComputador),
                    FOREIGN KEY (PostoId) REFERENCES Postos(Id)
                );";
            cmd.ExecuteNonQuery();
        }

        public void PrepararCodigoEmparelhamento(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo)) return;
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(codigo.Trim())));
            using var ligacao = Abrir();
            using var cmd = ligacao.CreateCommand();
            // Mantém o código de utilização única entre reinícios do servidor.
            // Se o administrador definir um código diferente na variável de ambiente,
            // substitui o hash anterior e reativa o emparelhamento.
            cmd.CommandText = @"INSERT INTO CodigoEmparelhamento
                (Id, CodigoHash, Consumido, CriadoEm) VALUES (1, $hash, 0, $agora)
                ON CONFLICT(Id) DO UPDATE SET
                    CodigoHash = excluded.CodigoHash,
                    Consumido = 0,
                    CriadoEm = excluded.CriadoEm
                WHERE CodigoEmparelhamento.CodigoHash <> excluded.CodigoHash;";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$agora", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        public (string Id, string Token)? EmparelharPosto(string codigo, string nome)
        {
            if (string.IsNullOrWhiteSpace(codigo) || string.IsNullOrWhiteSpace(nome)) return null;
            using var ligacao = Abrir();
            using var tx = ligacao.BeginTransaction();
            using var ler = ligacao.CreateCommand();
            ler.Transaction = tx;
            ler.CommandText = "SELECT CodigoHash, Consumido FROM CodigoEmparelhamento WHERE Id = 1;";
            using var reader = ler.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(1) != 0) return null;
            string esperado = reader.GetString(0);
            reader.Close();

            string recebido = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(codigo.Trim())));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(esperado), Convert.FromHexString(recebido))) return null;

            using var consumir = ligacao.CreateCommand();
            consumir.Transaction = tx;
            consumir.CommandText = "UPDATE CodigoEmparelhamento SET Consumido = 1 WHERE Id = 1 AND Consumido = 0;";
            if (consumir.ExecuteNonQuery() != 1) return null;

            string id = Guid.NewGuid().ToString("D");
            string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            string tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token)));
            using var inserir = ligacao.CreateCommand();
            inserir.Transaction = tx;
            inserir.CommandText = @"INSERT INTO Postos (Id, Nome, TokenHash, Ativo, CriadoEm)
                VALUES ($id, $nome, $hash, 1, $agora);";
            inserir.Parameters.AddWithValue("$id", id);
            inserir.Parameters.AddWithValue("$nome", nome.Trim());
            inserir.Parameters.AddWithValue("$hash", tokenHash);
            inserir.Parameters.AddWithValue("$agora", DateTime.UtcNow.ToString("O"));
            inserir.ExecuteNonQuery();
            tx.Commit();
            return (id, token);
        }


        /// <summary>
        /// Regista (ou renova a credencial de) um posto autenticado pelo Windows.
        /// A identidade Windows é obtida pelo servidor; o cliente só indica o nome do computador.
        /// </summary>
        public (string Id, string Token) RegistarPostoWindows(string identidadeWindows, string nomeComputador, string? credencialAtual = null)
        {
            identidadeWindows = (identidadeWindows ?? string.Empty).Trim();
            nomeComputador = (nomeComputador ?? string.Empty).Trim();
            if (identidadeWindows.Length == 0 || nomeComputador.Length == 0)
                throw new ArgumentException("A identidade Windows e o nome do computador são obrigatórios.");

            using var ligacao = Abrir();
            using var tx = ligacao.BeginTransaction();

            string? id = null;
            using (var procurar = ligacao.CreateCommand())
            {
                procurar.Transaction = tx;
                procurar.CommandText = @"SELECT PostoId FROM PostosWindows
                    WHERE IdentidadeWindows = $identidade COLLATE NOCASE
                      AND NomeComputador = $nome COLLATE NOCASE;";
                procurar.Parameters.AddWithValue("$identidade", identidadeWindows);
                procurar.Parameters.AddWithValue("$nome", nomeComputador);
                id = procurar.ExecuteScalar() as string;
            }

            // Se o Launcher já possui uma credencial válida deste mesmo posto, reutiliza-a.
            // Evita invalidar a sessão da aplicação quando o Launcher é executado novamente.
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(credencialAtual))
            {
                using var lerHash = ligacao.CreateCommand();
                lerHash.Transaction = tx;
                lerHash.CommandText = "SELECT TokenHash FROM Postos WHERE Id = $id AND Ativo = 1;";
                lerHash.Parameters.AddWithValue("$id", id);
                string? hashGuardado = lerHash.ExecuteScalar() as string;
                string hashRecebido = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(credencialAtual.Trim())));

                if (!string.IsNullOrWhiteSpace(hashGuardado) &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(hashGuardado),
                        System.Text.Encoding.UTF8.GetBytes(hashRecebido)))
                {
                    using var atualizarAcesso = ligacao.CreateCommand();
                    atualizarAcesso.Transaction = tx;
                    atualizarAcesso.CommandText = @"UPDATE Postos
                        SET Nome = $nome, UltimoAcesso = $agora
                        WHERE Id = $id;";
                    atualizarAcesso.Parameters.AddWithValue("$nome", nomeComputador);
                    atualizarAcesso.Parameters.AddWithValue("$agora", DateTime.UtcNow.ToString("O"));
                    atualizarAcesso.Parameters.AddWithValue("$id", id);
                    atualizarAcesso.ExecuteNonQuery();

                    tx.Commit();
                    return (id, credencialAtual.Trim());
                }
            }

            string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token)));
            string agora = DateTime.UtcNow.ToString("O");

            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("D");
                using var inserirPosto = ligacao.CreateCommand();
                inserirPosto.Transaction = tx;
                inserirPosto.CommandText = @"INSERT INTO Postos
                    (Id, Nome, TokenHash, Ativo, CriadoEm, UltimoAcesso)
                    VALUES ($id, $nome, $hash, 1, $agora, $agora);";
                inserirPosto.Parameters.AddWithValue("$id", id);
                inserirPosto.Parameters.AddWithValue("$nome", nomeComputador);
                inserirPosto.Parameters.AddWithValue("$hash", hash);
                inserirPosto.Parameters.AddWithValue("$agora", agora);
                inserirPosto.ExecuteNonQuery();

                using var inserirWindows = ligacao.CreateCommand();
                inserirWindows.Transaction = tx;
                inserirWindows.CommandText = @"INSERT INTO PostosWindows
                    (IdentidadeWindows, NomeComputador, PostoId, CriadoEm)
                    VALUES ($identidade, $nome, $id, $agora);";
                inserirWindows.Parameters.AddWithValue("$identidade", identidadeWindows);
                inserirWindows.Parameters.AddWithValue("$nome", nomeComputador);
                inserirWindows.Parameters.AddWithValue("$id", id);
                inserirWindows.Parameters.AddWithValue("$agora", agora);
                inserirWindows.ExecuteNonQuery();
            }
            else
            {
                using var atualizar = ligacao.CreateCommand();
                atualizar.Transaction = tx;
                atualizar.CommandText = @"UPDATE Postos
                    SET Nome = $nome, TokenHash = $hash, Ativo = 1, UltimoAcesso = $agora
                    WHERE Id = $id;";
                atualizar.Parameters.AddWithValue("$nome", nomeComputador);
                atualizar.Parameters.AddWithValue("$hash", hash);
                atualizar.Parameters.AddWithValue("$agora", agora);
                atualizar.Parameters.AddWithValue("$id", id);
                if (atualizar.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("O posto Windows associado não existe na tabela de postos.");
            }

            tx.Commit();
            return (id, token);
        }

        public bool ValidarCredencialPosto(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token.Trim())));
            using var ligacao = Abrir();
            using var cmd = ligacao.CreateCommand();
            cmd.CommandText = @"UPDATE Postos SET UltimoAcesso = $agora
                WHERE TokenHash = $hash AND Ativo = 1;";
            cmd.Parameters.AddWithValue("$agora", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$hash", hash);
            return cmd.ExecuteNonQuery() == 1;
        }
    }
}
