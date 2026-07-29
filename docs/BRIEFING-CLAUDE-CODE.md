# Briefing FluidRuntime / FluidGateway

Handoff atualizado em 2026-07-29 para o release candidate v0.12.0.

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

Ainda nao e real:

- injection/attach em jogos ou processos externos;
- scheduler de threads do Windows;
- residencia fisica RAM/VRAM, PCIe bytes ou unified memory;
- texturas/boxes/pitches, buffers dynamic, `UpdateSubresource1` ou batching;
- fences, command lists, deferred contexts e todos os shader writes;
- atuacao no presentation path, D3D12 ou Vulkan;
- claim geral de FPS, energia ou maquinas antigas.

## 4. Contrato v0.12

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

## 5. Evidencia local v0.12

- managed tests: 79/79;
- CTests Release: 9/9;
- CTests Debug: 9/9;
- matriz negativa Release/Debug: 320/320;
- contrato exato de CI executado localmente: passou;
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

## 6. Contratos ABI

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

## 7. Comandos de verificacao

```powershell
dotnet test FluidRuntime.slnx -c Release
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

## 8. Evidencia

- [v0.12.0 report](evidence/v0.12.0-update-upload-elision.md)
- [RX 580 trace](evidence/traces/update-upload-elision-rx580-v0.12.0.json)
- [WARP trace](evidence/traces/update-upload-elision-warp-v0.12.0.json)
- [policy matrix](evidence/traces/control-policy-matrix-v0.12.0.json)
- [architecture](architecture.md)
- [roadmap](roadmap.md)

## 9. Proximo passo recomendado

Generalizar upload com seguranca: texturas e pitches canonicos, boxes parciais,
`UpdateSubresource1`, buffers dynamic, aliases, batching, fences e deferred
contexts. O cache nao pode crescer sem limite e cada novo padrao precisa de
equivalencia, regressao, budget, expiracao e rollback proprios.

External observation vem depois, com allowlist, consentimento, identidade do
executavel, recusa de anti-cheat/elevated/protected e modo read-only antes de
qualquer atuacao.

Mensagem curta: v0.12 prova interferencia especifica e reversivel em uploads
`UpdateSubresource` owned, com bytes exatos, geracao protegida e intervalos CPU
e GPU menores na RX 580. Nao alargue esse claim para RAM/VRAM fisica ou jogos.
