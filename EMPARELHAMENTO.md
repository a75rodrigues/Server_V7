# Emparelhamento de postos UHD

O servidor aceita um código de emparelhamento de utilização única através da variável de ambiente `UHD_PAIRING_CODE`.

## Preparar um código no servidor Windows

Abrir PowerShell na máquina do servidor e executar:

```powershell
$codigo = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$env:UHD_PAIRING_CODE = $codigo
Write-Host "Código de emparelhamento (entregar ao administrador do posto):"
Write-Host $codigo
dotnet run
```

O código é lido quando o servidor arranca. Não o colocar em ficheiros versionados nem o enviar por canais não seguros. Entregar o código apenas ao administrador do posto que vai ser emparelhado.

## Continuar a utilizar a chave de acesso antiga

O método antigo continua disponível. Na aplicação, abrir **Administração > Servidor**, expandir **Configuração técnica do servidor** e introduzir no campo **Chave de acesso** o mesmo valor de `ChaveAcesso` definido no `appsettings.json` do servidor. Guardar as definições.

Por segurança, a aplicação não volta a mostrar a chave depois de a guardar. O campo aparecer vazio quando se reabre o ecrã é intencional: o valor continua guardado localmente e é reutilizado nos pedidos. O texto de estado junto ao campo indica se existe uma chave guardada ou se ainda é necessário introduzi-la.

## Emparelhar a aplicação

1. Abrir a aplicação UHD e iniciar sessão com um utilizador autorizado a administrar o servidor.
2. Abrir **Administração > Servidor** e expandir **Configuração técnica do servidor**.
3. Confirmar o endereço do servidor e o nome deste posto.
4. Introduzir o código recebido do administrador do servidor.
5. Selecionar **Emparelhar este posto**.

O servidor aceita o código uma única vez e devolve uma credencial aleatória. A aplicação guarda essa credencial em `%LOCALAPPDATA%\UHD\posto.dat`, protegida pelo DPAPI do utilizador Windows atual. A base de dados central guarda apenas o hash da credencial.

Depois do emparelhamento, o posto autentica os pedidos com a credencial individual; a chave legada `ChaveAcesso` deixa de ser necessária nesse posto.

## Gerar outro código

Para emparelhar outro posto, definir um código novo na variável `UHD_PAIRING_CODE` e reiniciar o servidor. O servidor deteta que o código mudou e reativa automaticamente o emparelhamento. Reutilizar o código anterior não volta a abrir o emparelhamento.

## Notas importantes

- O URL descoberto pelo launcher continua a ser guardado separadamente; o launcher não transporta chaves nem códigos de emparelhamento.
- A credencial DPAPI fica associada ao utilizador Windows que executou o emparelhamento. Se a aplicação passar a ser executada com outra conta Windows, será necessário emparelhar esse utilizador/posto novamente.
- Não apagar `posto.dat` enquanto se pretender manter a autenticação individual desse posto.
