using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace UHD.Sincronizacao
{
    // Este ficheiro é IGUAL na aplicação e no servidor: descreve o que viaja entre os dois.

    /// <summary>Um registo (utilizador, doente, avaliação ou versão) a enviar ou a receber.</summary>
    public class AlteracaoDto
    {
        public string Tabela { get; set; } = string.Empty;
        public string Chave { get; set; } = string.Empty;
        public JsonObject Dados { get; set; } = new JsonObject();

        /// <summary>Nº de ordem atribuído pelo servidor (só nas alterações recebidas).</summary>
        public long Seq { get; set; }
    }

    /// <summary>O servidor reconheceu que dois doentes são o mesmo (mesmo nº de processo): "De" passa a ser "Para".</summary>
    public class SubstituicaoDto
    {
        public string Tabela { get; set; } = string.Empty;
        public string De { get; set; } = string.Empty;
        public string Para { get; set; } = string.Empty;
        public JsonObject? Dados { get; set; }
    }

    public class PedidoSincronizacao
    {
        /// <summary>Identificador único deste posto (não é o nome do computador).</summary>
        public string Posto { get; set; } = string.Empty;

        /// <summary>Último nº de ordem do servidor que este posto já recebeu.</summary>
        public long Cursor { get; set; }

        public List<AlteracaoDto> Alteracoes { get; set; } = new List<AlteracaoDto>();
    }

    public class RespostaSincronizacao
    {
        public List<AlteracaoDto> Alteracoes { get; set; } = new List<AlteracaoDto>();
        public List<SubstituicaoDto> Substituicoes { get; set; } = new List<SubstituicaoDto>();
        public long Cursor { get; set; }

        /// <summary>true = há mais alterações para receber (o posto volta a pedir).</summary>
        public bool TemMais { get; set; }
    }
}
