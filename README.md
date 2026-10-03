# Kroton.ia - Game Mode Optimizer

## O que e?
Otimizador automatico de performance para gaming para Windows 10/11. Detecta quando voce esta jogando, desativa servicos desnecessarios para liberar CPU/RAM, e **restaura tudo automaticamente** ao sair do jogo.

## Caracteristicas
- Deteccao de 18+ jogos configuraveis (Fortnite, Overwatch, Rainbow Six, Euro Truck, Terraria, etc)
- Modo jogo ativado/desativado **automaticamente** ao iniciar/fechar o jogo
- Restauracao exata via **backup + rollback** (estado original preservado)
- Inicia automaticamente com o Windows (tarefa agendada, sem consumo extra)
- Deteccao por HashSet (~23ms por ciclo, ~0.1% de CPU)
- **Guard de memoria no modo jogo**: libera a RAM de processos nao prioritarios a cada 3
  ciclos e, quando a RAM chega a **97% (travando)**, **encerra** processos de fundo
  tambem - nunca o jogo, Discord/Spotify, navegador com musica tocando, criticos do
  Windows ou qualquer app com janela aberta
- **Auto-update por GitHub Releases**: verifica atualizacoes em segundo plano e instala
  pacotes novos automaticamente quando o sistema estiver fora do modo jogo
- 100% reversivel: desinstalacao limpa restaura os servicos

## Requisitos
- Windows 10 ou 11
- PowerShell 5.1 (ja instalado em todo Windows 10/11 - nada extra para instalar)
- Permissoes de Administrador (para instalar e alterar servicos)

## Instalacao

### Passo 1: Preparar
Extraia a pasta do projeto em qualquer local. Estes arquivos devem estar presentes:

- install\Install-Kroton.bat
- install\Install-Kroton.ps1
- KrotonIA.exe (interface grafica)
- KrotonGameModeRunner.ps1
- Optimize-BackgroundServices.ps1
- game-mode-config.json
- service-profile.json (auditoria)
- service-profile.game.json (modo jogo)
- service-profile.normal.json (modo normal/fallback)
- service-profile.light.json (modo leve - somente telemetria)
- app\List-CatalogServices.ps1 (catalogo de servicos usado pela GUI)

### Passo 2: Instalar
1. Clique com botao direito em **install\Install-Kroton.bat**
2. Escolha **"Executar como administrador"**
3. Escolha a opcao **[1] Instalacao padrao + tarefa agendada SYSTEM**
4. Selecione a pasta de destino no seletor. Selecionar `C:\Program Files` usa o padrao
   `C:\Program Files\KrotonIA`; escolha ou crie outra pasta para instalar em um local
   personalizado.
5. O instalador copia os arquivos + `KrotonIA.exe`, registra a tarefa agendada, cria o
   atalho na Area de Trabalho, inicia o monitor e registra no Painel de Controle.

**Alternativa sem o .bat:** abra o **KrotonIA.exe** e use o menu **Kroton.ia > Instalar / Atualizar...**
- O mesmo instalador (`install\Install-Kroton.ps1`) roda por tras, com pedido de UAC se preciso
- Escolha a pasta no seletor; apos instalar em outra pasta, a GUI oferece reabrir a
  interface de la (mantem GUI e monitor na mesma pasta)

### Passo 3: Pronto!
- O computador passa a monitorar jogos
- Inicia automaticamente com o Windows
- Nenhuma outra acao necessaria

## Como funciona

## Atualizacao automatica

A interface consulta a ultima GitHub Release no repositorio `Gian-251/Kroton.ia-releases` no
maximo a cada 6 horas. Para publicar uma atualizacao, a release precisa ter uma tag
SemVer (`vMAJOR.MINOR.PATCH`), um asset chamado exatamente `Install-Kroton.zip`, e a
versao do assembly em `app\Program.cs` deve corresponder a tag. O pacote e validado e
extraido em uma pasta de staging antes de alterar a instalacao.

A aplicacao so inicia o instalador quando `runtime\game-mode-state.json` estiver em
modo `normal` e nenhum processo da lista de jogos estiver ativo por pelo menos 30
segundos. A janela fecha durante a substituicao e a nova versao e reaberta ao terminar.
Se a condicao segura nao ocorrer em 24 horas, a tentativa e adiada para uma proxima
inicializacao.

O atualizador **nao envia dados ao GitHub**. `runtime\`, `backup\`, `game-mode-config`
e os perfis locais nao sao substituidos pelo pacote. Antes da copia, eles tambem sao
salvos localmente em `%LOCALAPPDATA%\KrotonIA\update-backups\`; arquivos da aplicacao
anteriores ficam no mesmo backup para rollback em caso de falha. O log fica em
`runtime\github-update.log`.

O pacote inclui `app\Sign-App.ps1` como ferramenta opcional para assinar o executavel e
gerar um catalogo de scripts. Ela exige um certificado de assinatura e o Windows SDK;
nenhum certificado ou senha e incluido. Assinar o binario nao garante por si so a
reputacao necessaria para remover alertas do SmartScreen em maquinas novas.

`app\Build-App.ps1` registra um marcador local em
`%LOCALAPPDATA%\KrotonIA\developer-mode.txt`, associado ao caminho compilado. Quando
esse caminho e executado, a verificacao automatica e ignorada; o marcador nao faz parte
do pacote distribuido e nao desativa atualizacoes de uma instalacao em outro caminho.

**Modo Normal (padrao):**
- Todos os servicos no estado original

**Modo Jogo (ativado automaticamente):**
- Desativa servicos candidatos (Xbox, telemetria, busca, fax, mapas, SysMain etc)
- Preserva criticos: Defender, rede, audio, DNS, firewall, updates
- Preserva Rainmeter e TranslucentTB

**Restauracao (ao sair do jogo):**
- Rollback do backup salvo ao entrar no modo jogo
- Servicos voltam exatamente ao estado original (startup type + se estava rodando)

**Deteccao:**
- Verifica a cada 20 segundos (configuravel em game-mode-config.json)
- ~23ms por ciclo

## Modos de comportamento (menu "Comportamento" na GUI)

| Modo | O que faz |
|---|---|
| Automatico (Modo Jogo) | Detecta o jogo e aplica o perfil completo (Xbox, busca, SysMain, fax, mapas, telemetria); restaura tudo ao sair do jogo |
| Modo Leve (Apenas Telemetria) | Detecta o jogo e desativa **somente** os servicos de telemetria (`service-profile.light.json`: DiagTrack, dmwappushservice, Diagnostic Hub, WER); restaura ao sair |
| Desativado (Apenas Monitorar) | Nao altera servico algum; o runner apenas monitora jogos e o estado do sistema |

A GUI mostra a descricao do modo selecionado logo abaixo do combo. A mudanca vale no proximo ciclo do runner (maximo 20s), e gravada em `activeMode` no `game-mode-config.json` e exibe uma janela de confirmacao com o que o modo faz.

## Menu do instalador (install\Install-Kroton.bat)

| Opcao | Funcao |
|---|---|
| 1 | Instalacao padrao + tarefa agendada SYSTEM (recomendado) |
| 2 | Instalacao + tarefa agendada por usuario |
| 3 | Instalacao + servico Windows (NSSM, requer internet) |
| 4 | Instalar somente os arquivos |
| 5 | Auditoria (lista servicos e recomendacoes, nao altera nada) |
| 6 | Aplicar modo jogo agora |
| 7 | Restaurar estado original (rollback) |
| 8 | Sair |


## Interface grafica (KrotonIA.exe)

O projeto inclui um aplicativo WinForms compilado para **.NET Framework 4.x (ja presente no Windows 10/11)** - nenhuma instalacao extra necessaria.

**Recursos:**
- Status em tempo real: modo atual (Normal/Jogo) + estado do monitor
- **Auditar servicos**: lista os candidatos com confianca (somente leitura)
- **Modo jogo agora** / **Restaurar original**: aplicacao manual com confirmacao
- **Lista de jogos**: adicionar/remover/salvar direto no `game-mode-config.json`
- **Log**: ultimas 300 linhas do `runtime\game-mode.log`
- Atualizacao automatica do status (5s com a janela aberta; 30s minimizado - a checagem da tarefa agendada roda a cada ~30s ou ao clicar "Atualizar" para aliviar a CPU)
- **Menu Kroton.ia** no topo: **Instalar / Atualizar...** (instala pelo proprio executavel, sem o `.bat`) e **Sair** (saida real)
- **Bandeja do Windows**: fechar (X) ou minimizar leva a interface para a bandeja e o monitor continua ativo; o **icone mostra o % de RAM em tempo real** (verde/amarelo/vermelho), o tooltip traz RAM + modo atual, e o menu tem Abrir / Ver memoria por aplicativo / Sair
- **Modo jogo informativo**: ao entrar no modo jogo, a GUI e o `runtime\game-mode.log` informam **quantos servicos foram parados** (`servicesStopped` em `runtime\game-mode-state.json`)
- Icone proprio no executavel, na janela, no atalho e no Painel de Controle
- **Menu Comportamento** no cabecalho: Automatico (Modo Jogo) / Modo Leve / Desativado - grava `activeMode` no `game-mode-config.json` e o runner reage no proximo ciclo (maximo 20s). Com `disabled` o runner restaura (se estava em modo jogo) e para de alterar servicos
- **Aba Servicos do Windows**: catalogo completo dos servicos da maquina com colunas Servico / O que ele faz / Categoria / Estado / Inicializacao / Acao
  - Servicos criticos aparecem como **Protegido** (nao podem ser alterados)
  - Filtro por categoria + busca por texto (nome, descricao)
  - Acoes por servico: **Parar/Iniciar** (estado) e **Automatico/Manual** (inicializacao), com confirmacao
- **Buscar descricoes na Web**: preenche descricoes faltantes via Microsoft Learn (pt-BR) e Wikipedia, com cache em `runtime\services-desc-cache.json`

### Logo e icone
- Origem: `app\kroton.png` (1254x1254, fundo transparente) - fica no repositorio, nao vai no pacote
- `app\kroton.ico` - icone multirresolucao (16/24/32/48/64/128/256) embutido no `.exe`
- `app\kroton-logo.png` - logo 64x64 embutida como recurso e exibida no cabecalho da janela
- Para regerar o `.ico`/logo a partir de outro PNG: `powershell -ExecutionPolicy Bypass -File app\Make-Icon.ps1`

**Uso:**
- Duplo clique em `KrotonIA.exe` (eleva automaticamente via UAC)
- `KrotonIA.exe --selftest` - validacao automatica sem GUI (exit 0 = OK)
- `KrotonIA.exe --no-elevate` - modo desenvolvimento sem elevacao

**Recompilar apos alterar o codigo-fonte (`app\`):**

```powershell
powershell -ExecutionPolicy Bypass -File app\Build-App.ps1
```

Fontes: `app\Program.cs`, `app\MainForm.cs`, `app\ScriptRunner.cs`, `app\app.manifest`, `app\kroton.ico`, `app\kroton-logo.png`

## Desinstalar

### Opcao 1: Painel de Controle (Recomendado)
1. Abra **Painel de Controle > Programas e Recursos**
2. Procure **"Kroton.ia"** e clique em **Desinstalar**

### Opcao 2: Script
1. Clique com botao direito em **install\Uninstall-Kroton.bat**
2. Escolha **"Executar como administrador"**
3. Digite **sim** na confirmacao

A desinstalacao restaura os servicos (rollback do backup) antes de remover os arquivos.

## Configuracao

### Adicionar jogos
Edite `game-mode-config.json` e adicione o nome do executavel (sem .exe ou com, ambos funcionam):

```json
"gameProcessNames": [
    "FortniteClient-Win64-Shipping",
    "Overwatch",
    "seu-novo-jogo"
]
```

A mudanca vale na proxima verificacao (maximo 20 segundos). Para descobrir o nome do processo, abra o Gerenciador de Tarefas > Detalhes.

### Modificar servicos
- `service-profile.game.json` - servicos desativados no modo jogo
- `service-profile.normal.json` - servicos reativados no modo normal (fallback)

Regras por lista:
- `candidateNamePrefixes`: servicos que PODEM ser desativados
- `criticalNamePrefixes`: servicos que NUNCA devem ser tocados
- `keepRunningNamePrefixes`: servicos que DEVEM continuar rodando

**Importante:** todo candidato do modo jogo deve existir tambem no perfil normal, senao nao sera reativado no fallback.

### Guard de memoria (`memoryGuard` em `game-mode-config.json`)

| Chave | Padrao | O que faz |
|---|---|---|
| `enabled` | `true` | liga/desliga o guard (roda SO no modo jogo) |
| `minFreeMb` | `1024` | com menos RAM livre que isso, comeca a liberar working set |
| `everyCycles` | `3` | de quantos em quantos ciclos do runner roda |
| `minProcessMb` | `300` | ignora processos menores que isso |
| `pressurePercent` | `90` | **Memory Pressure**: limpa apenas quando o uso de RAM atinge X% (0 = so o `minFreeMb`) |
| `stallCheck` | `true` | **frametime**: permite limpar com RAM folgada se o jogo estiver sofrendo stutter |
| `stallPageInputMin` | `50` | limiar de hard page faults/s do SO (jogo da lista rodando) para considerar frametime instavel |
| `priorityNames` | Discord, Spotify... | nomes que NUNCA sao tocados (aceita curinga `*`) |
| `whitelistNames` | Steam, Battle.net... | lista de exclusao EXTRA, soma ao `priorityNames` (apps criticos do usuario) |
| `targetNames` | chrome, msedge... | lista de ALVOS: preenchida, SOMENTE estes entram no trim/kill; vazia = qualquer elegivel |
| `killEnabled` | off no script | **encerra** processos quando a RAM chega ao limiar (o config do repo habilita) |
| `killRamPercent` | `97` | limiar de colapso/travamento (%) |
| `killMaxPerCycle` | `3` | tampa de processos encerrados por execucao |

Protegidos em TODOS os casos: o jogo ativo, `priorityNames` + `whitelistNames` (lista de
exclusao do usuario), processos com sessao de
audio ativa (navegador tocando musica, Discord, Spotify), o processo PRINCIPAL dos
navegadores e os criticos do Windows. Com `targetNames` preenchido, **somente** os
processos da lista (que NAO estejam protegidos) sao trimados/kilados - vazia, vale a
regra antiga: qualquer elegivel. No **kill** vale uma regra extra: apenas
processos **SEM janela visivel** - nunca e' derrubado um app com trabalho aberto
(Word, IDE etc.). **Memory Pressure** (gatilho do trim): o guard so' limpa quando a RAM
esta' sob pressao - uso >= `pressurePercent` (90%) OU livre < `minFreeMb` - ou, com a
RAM folgada, quando o **frametime** do jogo provavelmente esta' instavel (jogo da lista
rodando + SO pagando paginas para disco, `PagesInputPersec` >= `stallPageInputMin` =
stutter causado por falta de memoria). `-Force` (botao) e o kill armado ignoram o gate.
Tudo e' registrado em `runtime\memory-guard.log` com linhas `[MEM]`
(`gatilho=forca|kill|pressao|stall`, liberacao de working set) e `[KILL]`
(encerramentos, com RAM% e limiar).

## Assinatura de código (SmartScreen)

O `KrotonIA.exe` e os scripts `.bat`/`.ps1` podem ser assinados com **Authenticode** para reduzir avisos do Windows SmartScreen ("Windows protected your PC") em máquinas novas.

### Estado atual
- **Certificado auto-assinado de dev** (`CN=Kroton.ia Dev, O=Kroton.ia`) incluído no repositório — válido até **2031-10-03**.
- `app\Sign-App.ps1` assina o exe e cria um catálogo (`KrotonScripts.cat`) para os scripts.
- Em **máquinas de desenvolvimento** (com o cert instalado em `TrustedRoot`): sem avisos.
- Em **máquinas novas** (sem o cert): SmartScreen ainda avisa — o cert auto-assinado **não** constrói reputação.

### 3 caminhos para remover avisos SmartScreen em produção

| Opção | Custo | Reputação SmartScreen | Esforço |
|---|---|---|---|
| **1. Self-signed** (atual) | $0 | ❌ nenhuma (só dev local) | Já feito |
| **2. SignPath.io** (OSS gratuito) | $0 | ⏳ constrói ao longo do tempo | Médio (integrar com GitHub Actions) |
| **3. EV Code Signing Cert** | ~$200–400/ano | ✅ imediata | Alto (verificação de identidade + token USB) |

**Recomendação**: para distribuição pública, use **SignPath.io** (gratuito para projetos open-source no GitHub) ou compre um **EV Code Signing Cert** (DigiCert, Sectigo, GoGetSSL).

### Assinar localmente (dev)
```powershell
# Gera um novo cert auto-assinado (5 anos)
New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Kroton.ia Dev, O=Kroton.ia" `
    -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) `
    -HashAlgorithm SHA256 -KeyExportPolicy Exportable

# Instala no TrustedRoot (só nesta máquina)
$thumb = "A049C2CB57D08D7C417F5E576FBE34BB9491DC24"
Export-Certificate -Cert "Cert:\CurrentUser\My\$thumb" -FilePath "$env:TEMP\kroton-root.cer"
Import-Certificate -FilePath "$env:TEMP\kroton-root.cer" -CertStoreLocation "Cert:\CurrentUser\Root"

# Assina o exe + scripts
.\app\Sign-App.ps1 -CertThumbprint $thumb
```

### Validar assinatura
```powershell
Get-AuthenticodeSignature .\KrotonIA.exe
# Status: Valid, StatusMessage: Assinatura verificada.
```

## Troubleshooting

### Instalacao falha
- [ ] Rodou como Administrador? (obrigatorio)
- [ ] Todos os arquivos estao na mesma pasta?
- [ ] Windows 10/11 com PowerShell 5.1? (padrao)

### Deteccao nao funciona
- [ ] O .exe do jogo esta em game-mode-config.json?
- [ ] Abra o Gerenciador de Tarefas > Detalhes e confira o nome exato do processo

### A GUI mostra "MODO: NORMAL" com o jogo aberto
- **Sintoma:** o jogo esta rodando, mas a interface exibe NORMAL (ou a aba Log aparece vazia) e o runner nao entra no modo jogo.
- **Causa raiz:** GUI e runner em **BaseDirs diferentes** - cada um le `runtime\game-mode-state.json` da PROPRIA pasta. Ex.: interface aberta em `d:\Kroton.ia` com a tarefa apontando para `C:\Program Files\KrotonIA` (ou vice-versa).
- **Como conferir:**
  1. Pasta do exe aberto: clique com botao direito no atalho > Propriedades > Caminho
  2. Pasta da tarefa: `(Get-ScheduledTask -TaskName 'KrotonIA-GameModeRunner').Actions.Arguments`
  3. BaseDir da interface: `KrotonIA.exe --selftest` e olhe a linha `BaseDir:` em `%TEMP%\kroton-selftest.log`
- **Correcao:** manter interface e tarefa na MESMA pasta:
  1. Se a tarefa aponta para `C:\Program Files\KrotonIA`: abra a interface pelo atalho da Area de Trabalho (nao pelo repositorio)
  2. Reapontar tudo de uma vez: menu **Kroton.ia > Instalar / Atualizar...** na GUI (ou `install\Install-Kroton.bat` > [1]) e reabra a interface instalada quando solicitado
  3. Reapontar manualmente (rodar como Administrador):
     ```powershell
     $a = New-ScheduledTaskAction -Execute 'PowerShell.exe' `
         -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "d:\Kroton.ia\KrotonGameModeRunner.ps1"'
     Set-ScheduledTask -TaskName 'KrotonIA-GameModeRunner' -Action $a
     ```

### Servicos nao restauram
- Execute install\Install-Kroton.bat > opcao **[7] Restaurar estado original (rollback)**
- Ou reinicie o computador

### Ver logs
- `runtime\game-mode.log` - historico de mudancas de modo
- `runtime\game-mode-state.json` - estado atual (normal/game)

## Desempenho

- CPU: ~0.1% em repouso
- Memoria: ~5MB
- Servicos desativados no modo jogo: 30-40 (varia por maquina)
- Impacto real no gaming: 5-15% FPS (varia por jogo e hardware)

## Perguntas frequentes

**P: Posso customizar quais servicos desativar?**
R: Sim, edite os perfis .json. Mantenha os criticos na lista de protegidos.

**P: E se algo der errado?**
R: Todas as mudancas sao reversiveis. Use a opcao [7] (rollback) ou reinicie.

**P: Posso rodar 2 instalacoes?**
R: Nao, cria conflito. Use apenas uma.

**P: Como desativo temporariamente?**
R: Agendador de Tarefas > KrotonIA-GameModeRunner > Desabilitar

## Estrutura do projeto

```
Kroton.ia/
├── KrotonIA.exe                  # Aplicativo grafico (WinForms)
├── KrotonGameModeRunner.ps1      # Monitor de jogos (loop principal)
├── Optimize-BackgroundServices.ps1  # Motor de auditoria/apply/rollback
├── game-mode-config.json         # Lista de jogos + intervalo
├── service-profile.json          # Perfil de auditoria
├── service-profile.game.json     # Perfil modo jogo
├── service-profile.normal.json   # Perfil modo normal
├── service-profile.light.json    # Perfil modo leve (somente telemetria)
├── install/                      # Instalador e desinstalador
│   ├── Install-Kroton.bat        # Instalador (menu)
│   ├── Install-Kroton.ps1        # Instalador (PowerShell puro)
│   ├── Install-KrotonService.ps1 # Variante servico Windows (NSSM)
│   ├── Install-KrotonStartup.ps1 # Variante tarefa por usuario
│   ├── Uninstall-Kroton.bat      # Desinstalador (wrapper)
│   ├── Uninstall-Kroton.ps1      # Desinstalador (restaura + remove)
│   ├── Uninstall-KrotonService.ps1
│   └── Uninstall-KrotonStartup.ps1
├── app/                          # Fontes C#, Build-App.ps1, Make-Icon.ps1, List-CatalogServices.ps1, Detect-Games.ps1, logo/icone
├── dist/                         # Pacotes .zip prontos para distribuicao
├── backup/                       # Backups de servicos (gerado)
├── runtime/                      # Logs e estado (gerado)
└── tools/                        # NSSM etc. (baixado sob demanda)
```

## Licenca
Projeto Open Source - Use livremente

## Versao
Versao: 1.2
