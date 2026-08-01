# Briefing FluidRuntime / FluidGateway

Handoff atualizado em 2026-08-01 para o release v0.14.0.

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
  shared-memory IPC, control plane, workloads, atuacao e evidence gates.
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

Ainda nao e real:

- injection/attach em jogos ou processos externos;
- scheduler de threads do Windows;
- residencia fisica RAM/VRAM, PCIe bytes ou unified memory;
- texturas/boxes/pitches, buffers dynamic, `UpdateSubresource1` ou batching;
- fences, command lists, deferred contexts e todos os shader writes;
- atuacao no presentation path, D3D12 ou Vulkan;
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
- decisao Gateway e consultiva, sem autoridade sobre o ABI nativo.

Fingerprint do contrato:

`0d24d96aec32d74e123f9e198e51adde74ddf190e8c40b0ac18bddf5c4108b2f`

Fingerprint dos 17 golden vectors:

`3afb2a04373b1a21bd36fe9580c2adc95b38a619d1c4d8864205eaf45bcf6216`

## 5. Contrato nativo v0.12

Action bit 8 e exclusiva para um upload direto elegivel:

- recurso observado na criacao e com proveniencia confiavel;
- `D3D11_USAGE_DEFAULT`, buffer, subresource zero;
- update completo, box nulo, row/depth pitch zero;
- tamanho de 1..4 MiB;
- um unico recurso retido no cache;
- bytes exatos iguais e geracao do destino igual.

O target usa um buffer de 4 MiB e 67 updates diretos:

1. A obrigatorio e 32 A repetidos;
2. B com um bit alterado e 16 B repetidos;
3. `CopyResource` externo grava C;
4. B e reenviado obrigatoriamente e repetido mais 16 vezes.

Baseline encaminha 67/67. Optimized encaminha os tres obrigatorios e pula 64.
Com os tres updates legados, os totais nativos sao 70 forwarded no baseline e
6 no optimized. O destino final precisa ser B, diferente de A e C.

`memcmp` prova igualdade. FNV-1a apenas rotula eventos. Retirement e detach
apagam os bytes retidos.

## 6. Evidencia local v0.14

- managed tests: 113/113;
- FluidLink .NET: 34/34 entre v1 e v2;
- FluidGateway completo: 242/242;
- FluidGateway FluidLink: 43/43 entre v1 e v2;
- interop Python/.NET: 11/11 round trips v1 e 11/11 v2;
- mesmo fluxo: 3.189 bytes v1 contra 1.880 bytes v2, -41,05%;
- decisao v2 exata: 800 us e 67.108.864 bytes modelados;
- package `FluidLink.0.2.0.nupkg` inspecionado com DLL, README, contratos e golden;
- CTests Release: 9/9;
- CTests Debug: 9/9;
- matriz negativa Release/Debug: 320/320;
- contrato exato de CI executado localmente: passou;
- feature CI e main CI no GitHub: passaram;
- WARP: 4/4 raw runs, claim bloqueado;
- RX 580: 22/22 raw runs, 1 warmup + 10 pares medidos;
- smokes generic, manager, sustained, readback e staging-upload: passaram.

AMD Radeon RX 580 2048SP, LUID `000000000000d8c9`:

| Metrica | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU workload QPC | 309,334.000 us | 82,718.050 us | 333,514.890 us | 89,121.825 us |
| GPU timestamp interval | 260,434.700 us | 2,500.480 us | 275,276.644 us | 3,213.016 us |

CPU wins: 10/10. GPU wins: 10/10. Delta pareado CPU p50/p95: -73,442%
e -67,795%. Delta pareado GPU p50/p95: -99,046% e -98,831%.

Scope aprovado:

`owned-d3d11-default-buffer-full-update-subresource-exact-content-workload-only`

Base do claim:

`gpu-interval-improvement-with-bounded-cpu-content-comparison-overhead`

As linhas nativas continuam sendo a evidencia publicada da v0.12; a v0.14 nao
altera fonte nativa, ABI, policy de hook ou claim de hardware.

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

Generalizar upload com seguranca: texturas e pitches canonicos, boxes parciais,
`UpdateSubresource1`, buffers dynamic, aliases, batching, fences e deferred
contexts. O cache nao pode crescer sem limite e cada novo padrao precisa de
equivalencia, regressao, budget, expiracao e rollback proprios.

External observation vem depois, com allowlist, consentimento, identidade do
executavel, recusa de anti-cheat/elevated/protected e modo read-only antes de
qualquer atuacao.

Mensagem curta: v0.14 remove JSON do FluidLink, usa schemas posicionais,
opcodes/masks numericos e unidades inteiras, com 17 vetores cross-language e
41,05% menos bytes neste fluxo exato. A v0.12 continua sendo a prova de
interferencia especifica e reversivel em `UpdateSubresource` owned. Nenhum
desses resultados prova RAM/VRAM fisica, PCIe, FPS ou suporte a jogos externos.
