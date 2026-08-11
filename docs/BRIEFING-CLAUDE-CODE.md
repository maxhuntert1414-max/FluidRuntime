# Briefing FluidRuntime / FluidGateway

Handoff atualizado em 2026-08-11 para o release v0.18.0.

## 1. Objetivo geral

Construir um runtime open source que reduza desperdicio entre CPU, GPU, RAM,
VRAM, buffers, texturas e apresentacao. O projeto procura evitar copias,
sincronizacoes e trabalho redundante usando evidencia e atuacao reversivel.

Software nao transforma uma GPU discreta em memoria unificada fisica. A meta e
encurtar o caminho logico nas APIs disponiveis, sem vender equivalencia com
Apple Silicon nem prometer FPS antes da prova.

## 2. Repositorios

- `FluidGateway`: analise offline de PresentMon, diagnosticos, ranking, policy
  modeling e operational ledger.
- `FluidRuntime`: telemetria Windows/GPU/memoria, hook D3D11 cooperativo,
  observador D3D12 owned, shared-memory IPC, control plane, workloads, atuacao
  limitada e evidence gates.
- `FluidLink`: contrato binario compartilhado e cliente .NET tipado para
  eventos/decisoes consultivas entre os dois repositorios.

Diretorio local:

`C:\Users\maxhu\Documents\Trabalho\Project_FluidGateway`

Repositorios publicos:

- https://github.com/maxhuntert1414-max/FluidGateway
- https://github.com/maxhuntert1414-max/FluidRuntime

## 3. Nivel operacional atual

Real e verificado em software owned:

- telemetria de processo, RAM, WDDM VRAM e GPU engines;
- observacao de Present, recursos, Map/Unmap, updates, copies, clears e lifetime;
- ring IPC versionado e policy managed de um epoch/uma action/budget 1..128;
- elisao reversivel de `CopyResource` generica;
- readback `DEFAULT -> STAGING + CPU_READ`;
- upload `STAGING + CPU_WRITE -> DEFAULT`;
- upload direto full-buffer por `UpdateSubresource`, com comparacao exata;
- baseline/optimized pareado, hashes, adapter identity, timing e rollback.
- FluidLink loopback com header binario, opcodes numericos, fingerprint exato,
  sequencia/correlacao, heartbeat e payload posicional binario sem JSON.
- perfil batch opcional do FluidLink com ate 256 operacoes homogeneas em um
  request e vetor explicito ordenado; o perfil atual envia uma seed mais 128
  candidatas sem alterar o contrato v2 base.
- bridge fail-closed que transforma 128 decisoes live exatas do FluidGateway em
  budget nativo de action 8 somente no workload owned de `UpdateSubresource`.
- servidor Gateway loopback-only com oito workers, rejeicao por saturacao e
  deadlines absolutos para header, frame em progresso e sessao ociosa.
- observacao D3D12 owned de UPLOAD -> DEFAULT -> READBACK com COPY queue,
  promocao/barreira/decay, fence, conteudo exato, arquitetura e budgets DXGI.

Ainda nao e real:

- injection/attach em jogos ou processos externos;
- scheduler de threads do Windows;
- residencia fisica RAM/VRAM, PCIe bytes ou unified memory;
- texturas/boxes/pitches, buffers dynamic, `UpdateSubresource1` ou batching;
- command lists/fences fora do workload D3D12 owned, deferred contexts e todos
  os shader writes;
- atuacao no presentation path ou D3D12 e qualquer backend Vulkan;
- claim geral de FPS, energia ou maquinas antigas.

## 4. Contrato FluidLink v0.14

- header little-endian fixo de 56 bytes;
- opcodes de mensagem, evento e decisao em um byte cada;
- payload posicional por opcode, sem nomes de campos ou JSON no wire;
- presence/capability masks numericas e payload maximo de 65.535 bytes;
- tempo como microssegundos inteiros e memoria como bytes inteiros;
- UTF-8 estrito e limitado apenas nos campos realmente textuais;
- Hello/Welcome com SHA-256 exato do manifesto e capabilities obrigatorias;
- sequencia monotona, message ID, session ID e subject correlacionados;
- cliente .NET loopback-only com round trips concorrentes serializados;
- somente `runtime_event_rejected` preserva uma sessao valida;
- erro fatal do peer, framing ou correlacao invalida fecha a sessao;
- 17 frames dourados cobrem todos os opcodes de mensagem/evento, masks,
  endings, execute/deduplicate e um erro `invalid_payload`;
- endpoint JSONL legado fica em modo separado por conexao;
- decisoes Gateway continuam consultivas por padrao; a v0.15 tem uma unica
  traducao explicita para action 8, sem alterar o ABI e sem substituir `memcmp`.

Fingerprint do contrato:

`0d24d96aec32d74e123f9e198e51adde74ddf190e8c40b0ac18bddf5c4108b2f`

Fingerprint dos 17 golden vectors:

`3afb2a04373b1a21bd36fe9580c2adc95b38a619d1c4d8864205eaf45bcf6216`

Perfil batch opcional da v0.17:

- contrato SHA-256:
  `bf8727c22ac878ceff6dd0f462d6db5e81174737e839ecdf2e263a6f55268542`;
- capability bit 7, evento 105, decisao 7 e limite de 1..256 operacoes;
- batch ID de 16 bytes nao nulos, template posicional e vetor de mesma
  cardinalidade;
- falha parcial fecha a sessao sem retornar vetor parcial;
- quatro frames dourados compartilhados entre Python e .NET.

## 5. Contrato nativo v0.12

Action bit 8 e exclusiva para um upload direto elegivel:

- recurso observado na criacao e com proveniencia confiavel;
- `D3D11_USAGE_DEFAULT`, buffer, subresource zero;
- update completo, box nulo, row/depth pitch zero;
- tamanho de 1..4 MiB;
- um unico recurso retido no cache;
- bytes exatos iguais e geracao do destino igual.

O target padrao usa um buffer de 4 MiB e 131 updates diretos:

1. A obrigatorio e 64 A repetidos;
2. B com um bit alterado e 32 B repetidos;
3. `CopyResource` externo grava C;
4. B e reenviado obrigatoriamente e repetido mais 32 vezes.

Baseline encaminha 131/131. Optimized encaminha os tres obrigatorios e pula
128. Com os tres updates legados, os totais nativos sao 134 forwarded no
baseline e 6 no optimized. O destino final precisa ser B, diferente de A e C.
O perfil historico de 64 candidatas continua selecionavel para regressao.

`memcmp` prova igualdade. FNV-1a apenas rotula eventos. Retirement e detach
apagam os bytes retidos.

## 6. Evidencia local v0.18

- managed tests: 172/172;
- FluidGateway completo: 257/257;
- resiliencia do servidor: oito casos por dez repeticoes, 80/80;
- CTests Release: 13/13; CTests Debug: 13/13;
- WARP 128: 2/2 pares medidos, 128 skips e 536.870.912 bytes logicos por run;
- RX 580 128: 20/20 pares medidos mais um warmup, com conteudo, guards,
  accounting e rollback exatos;
- 21 autorizacoes hardware, 210 round trips e 2.688 decisoes candidatas;
- resposta malformada, stall e peer cumulativamente lento: baseline verificado,
  134 forwarded, zero skips e nenhuma policy;
- claim nativo owned aprovado; claim closed-loop continua bloqueado porque a
  autorizacao Gateway fica fora da janela de timing nativa;
- nenhum fingerprint FluidLink, ABI nativo, limite de cache ou teto de action
  foi ampliado.

Evidencia historica v0.17:

- managed tests: 157/157;
- FluidLink + autorizacao/process binding Gateway .NET: 59/59;
- FluidGateway completo: 249/249;
- FluidGateway FluidLink: 50/50 entre v1, v2 base e perfil batch;
- interop Python/.NET: 11/11 round trips v1 e 11/11 v2;
- mesmo fluxo: 3.189 bytes v1 contra 1.880 bytes v2, -41,05%;
- decisao v2 exata: 800 us e 67.108.864 bytes modelados;
- perfil batch: 65 operacoes em um request/vetor, 10 RTTs contra 74 na v0.15;
- por autorizacao: 1.168 bytes enviados e 1.970 recebidos, 3.138 total;
- package `FluidLink.0.3.0.nupkg` inspecionado com DLL, README, contratos e goldens;
- CTests Release: 12/12;
- CTests Debug: 12/12;
- matriz negativa Release/Debug: 320/320;
- contrato exato de CI executado localmente: passou;
- contrato local de release: passou; CI remota e gate obrigatorio apos o push;
- WARP batch: 2/2 autorizacoes, 64 skips e 268.435.456 bytes logicos por run;
- resposta batch malformada, stall e peer cumulativamente lento: baseline
  verificado, 70 forwarded, zero skips e nenhuma policy;
- RX 580: 22/22 raw runs, 1 warmup + 10 pares medidos;
- smokes generic, manager, sustained, readback e staging-upload: passaram.

D3D12 v0.16:

- WARP Release: 5/5; WARP Debug Layer: 5/5; RX 580 Release: 10/10;
- cada run: 4 MiB upload + 4 MiB readback logicos, um command list COPY, duas
  copias, uma barreira, fence 1 concluida e `memcmp` exato;
- DEFAULT nasce em COMMON, promove para COPY_DEST, transiciona para COPY_SOURCE
  e tem decay esperado para COMMON apos execute;
- Debug Layer: zero warnings e zero errors depois da correcao do initial state;
- PID do JSON precisa ser o processo lancado; target SHA-256 fica travado e e
  recalculado apos todos os runs;
- schema faltando/sobrando, adapter/arquitetura variavel, hash/fence/state
  divergente ou timestamp regressivo falha fechado;
- `performance_claim_allowed=false`: nao existe baseline otimizado e bytes
  logicos/DXGI budgets nao medem trafego fisico.

Closed loop v0.15:

- FluidGateway 0.64.0 live, FluidLink v2, contrato exato;
- 11 sessoes hardware, 814 round trips e 704 decisoes candidatas;
- cada optimized: action 8, budget 64, 64 skips e 268.435.456 bytes logicos;
- resposta malformada, TCP aceito sem resposta e peer valido cumulativamente
  lento: baseline novo, 70 forwarded, zero skips e zero policy publicada;
- deadline unico de 500 ms cobre connect, verificacao do peer e todos os RTTs;
- tuple IPv4 ligado pela tabela TCP do Windows ao PID/hash/start time esperados
  do Gateway; nome/versao anunciados sao metadata, nao autenticacao;
- target/hook abertos sem compartilhamento de escrita/delete antes da
  autorizacao, hashes congelados e binarios carregados revalidados antes da policy;
- contexto SHA-256 unico liga nonce, peer, target/hook, par/fase e action/budget;
- o report declara process binding verificado e autenticacao criptografica falsa;
- claim end-to-end bloqueado porque autorizacao esta fora da janela nativa.

AMD Radeon RX 580 2048SP, LUID `000000000000e28d`:

| Metrica | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU workload QPC | 325,056.300 us | 90,240.900 us | 349,183.625 us | 114,324.165 us |
| GPU timestamp interval | 274,956.460 us | 3,504.400 us | 296,985.492 us | 4,116.120 us |

CPU wins: 10/10. GPU wins: 10/10. Delta pareado CPU p50/p95: -71,857%
e -65,326%. Delta pareado GPU p50/p95: -98,764% e -98,463%.

Scope aprovado:

`owned-d3d11-default-buffer-full-update-subresource-exact-content-workload-only`

Base do claim:

`gpu-interval-improvement-with-bounded-cpu-content-comparison-overhead`

As linhas nativas medem somente o workload depois da autorizacao. FluidLink
teve p50/p95 de 34.643/94.087 us para 74 round trips, fora dessa janela. Por
isso a v0.15 prova closed loop funcional, mas nao libera claim de performance
end-to-end, FPS, RAM/VRAM fisica, PCIe ou jogos externos.

## 7. Contratos ABI

- attach options ABI 3;
- ring ABI 9, 2.048 slots, eventos de 80 bytes;
- snapshot ABI 12;
- control block ABI 1;
- action generic copy: 1;
- action readback copy: 2;
- action staging upload copy: 4;
- action direct UpdateSubresource: 8;
- primeiro bit desconhecido no negative matrix: 16.

Invariantes:

- target owned e opt-in;
- uma action exata por epoch;
- budget 1..128 e expiracao maxima de quatro segundos;
- zero overrun e zero sequencia perdida;
- conteudo, eventos, snapshot, policy, adapter e rollback precisam concordar;
- baseline e optimized em processos separados e ordem alternada;
- warmup fica no trace, mas fora da estatistica.

## 8. Comandos de verificacao

```powershell
dotnet test FluidRuntime.slnx -c Release
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/Test-FluidLinkIntegration.ps1 `
  -GatewayPath ..\FluidGateway
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/Test-GatewayManagedUpdateUpload.ps1 `
  -GatewayPath ..\FluidGateway `
  -CandidateActionCount 128 `
  -TrialPairs 20 `
  -WarmupPairs 1 `
  -Hardware $true
dotnet pack src/FluidLink/FluidLink.csproj -c Release `
  -o artifacts/packages
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
cmake --build native/build --config Debug
ctest --test-dir native/build -C Release --output-on-failure
ctest --test-dir native/build -C Debug --output-on-failure

dotnet run --project src/FluidRuntime -c Release -- update-upload-elision-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --trial-pairs 10 --warmup-pairs 1 `
  --hold-ms 50 --gpu-timeout-ms 5000 --hardware true `
  --out artifacts/update-upload-elision-hardware.json
```

## 9. Evidencia

- [v0.18.0 resiliencia e perfil de 128 actions](evidence/v0.18.0-resilience-update-upload-128.md)
- [v0.17.0 FluidLink operation batch](evidence/v0.17.0-fluidlink-operation-batch.md)
- [v0.16.0 D3D12 owned observation](evidence/v0.16.0-d3d12-observation.md)
- [v0.16.0 RX 580 D3D12 trace](evidence/traces/d3d12-observation-rx580-v0.16.0.json)
- [v0.16.0 WARP Debug trace](evidence/traces/d3d12-observation-warp-debug-v0.16.0.json)
- [v0.15.0 closed loop Gateway-managed](evidence/v0.15.0-gateway-managed-update-upload.md)
- [v0.15.0 RX 580 trace](evidence/traces/gateway-update-upload-rx580-v0.15.0.json)
- [v0.15.0 WARP trace](evidence/traces/gateway-update-upload-warp-v0.15.0.json)
- [v0.14.0 FluidLink v2 report](evidence/v0.14.0-fluidlink-v2.md)
- [v0.14.0 FluidLink v2 trace](evidence/traces/fluidlink-cross-process-v0.14.0.json)
- [v0.13.0 FluidLink report](evidence/v0.13.0-fluidlink-binary-interop.md)
- [v0.13.0 FluidLink trace](evidence/traces/fluidlink-cross-process-v0.13.0.json)
- [v0.12.0 report](evidence/v0.12.0-update-upload-elision.md)
- [RX 580 trace](evidence/traces/update-upload-elision-rx580-v0.12.0.json)
- [WARP trace](evidence/traces/update-upload-elision-warp-v0.12.0.json)
- [policy matrix](evidence/traces/control-policy-matrix-v0.12.0.json)
- [architecture](architecture.md)
- [roadmap](roadmap.md)

## 10. Proximo passo recomendado

O batch binario limitado agora carrega 129 operacoes em uma unica troca
request/vector, e o servidor nao permite que uma conexao lenta bloqueie as
demais. O proximo passo e incluir autorizacao e fallback no benchmark do loop
completo antes de qualquer decisao por frame. Shared memory so entra depois de
contrato de backpressure, identidade, crash recovery e benchmark.

Depois, generalizar upload com seguranca: texturas e pitches canonicos, boxes parciais,
`UpdateSubresource1`, buffers dynamic, aliases, batching, fences e deferred
contexts. O cache nao pode crescer sem limite e cada novo padrao precisa de
equivalencia, regressao, budget, expiracao e rollback proprios.

O primeiro caminho D3D12 owned ja existe e esta limpo no Debug Layer. Agora ele
precisa crescer para map/unmap, texturas, copy regions, placed resources,
aliases, multiplas queues e residency signals; depois vem proveniencia propria,
baseline/optimized e somente entao uma action limitada. Nao reutilizar as
suposicoes de geracao do D3D11 em estados/queues/fences explicitos.

Depois criar uma layer Vulkan opt-in separada para allocations/bindings,
copies, layouts, queue-family ownership, semaphores/fences e present. Cada
backend precisa de proveniencia, sincronizacao, equivalencia e rollback proprios.

External observation vem depois, com allowlist, consentimento, identidade do
executavel, recusa de anti-cheat/elevated/protected e modo read-only antes de
qualquer atuacao.

Mensagem curta: v0.18 endurece o servidor FluidLink e leva o workload owned ate
128 candidatas sem ampliar o ABI, o cache ou a autoridade nativa. Ainda nao ha
atuacao D3D12/Vulkan nem claim closed-loop de trafego fisico, FPS ou suporte a
jogos externos.
