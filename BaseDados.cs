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

            cmd.ExecuteNonQuery();
        }
    }
}
