# Briefing FluidRuntime / FluidGateway

Documento de handoff para continuidade em outro agente. Estado verificado em
2026-07-26.

## 1. Objetivo geral

Construir um runtime open source que reduza desperdicio no caminho entre CPU,
GPU, RAM, VRAM, buffers, texturas e apresentacao do frame. A tese do projeto e:

> O futuro da performance nao e so mais potencia. E menos desperdicio.

Nao e clone de DLSS, FSR ou Lossless Scaling. O destino e um gateway/scheduler
que tome decisoes cedo, evite transporte redundante e reverta qualquer atuacao
quando a evidencia piorar. Software nao reproduz memoria fisicamente unificada,
mas pode reduzir parte da distancia pratica entre os estagios expostos pelo SO e
pelas APIs graficas.

## 2. Arquitetura em duas metades

| Repo | Papel |
| --- | --- |
| [FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway) | Diagnostico PresentMon, evidencia, politica, ledger operacional e loop advisory |
| [FluidRuntime](https://github.com/maxhuntert1414-max/FluidRuntime) | Telemetria Windows/GPU, hook D3D11 cooperativo, control plane, atuacao owned e rollback |

Fluxo atual:

```text
PresentMon CSV
  -> FluidGateway (achados + ledger)
  -> FluidRuntime (.NET manager + telemetria)
  -> shared-memory control block
  -> native D3D11 hook (somente target owned com opt-in)
```

## 3. Nivel de operacao atual

Ja e real:

- diagnostico PresentMon offline em HTML/JSON;
- politica e daemon advisory com `would_modify_system=false`;
- probe read-only de processo, RAM, GPU e VRAM disponivel pelo Windows;
- observacao cooperativa de Present, recursos, escritas, copias, subresources,
  RTV/UAV clears e lifecycle D3D11;
- shared-memory ring ABI-v6 e control block ABI-v1;
- policy managed de um epoch, uma action mask e budget limitado a 1..128;
- interferencia reversivel em `CopyResource` redundante dentro do lab owned;
- comparacao baseline/optimized com eventos, snapshot, readback, hashes, timing e
  rollback obrigatorios.

Ainda nao e real:

- injecao ou attach em jogos/processos externos;
- scheduler de threads do Windows;
- controle de residencia RAM/VRAM;
- atuacao em presentation path;
- D3D12 ou Vulkan;
- claim geral de FPS, frame time, energia ou "salvar maquina velha".

## 4. Entrega v0.9.0

A v0.9.0 fecha duas etapas.

### Matriz negativa da policy

O comando `control-policy-matrix` cobre oito casos:

- valid;
- no opt-in;
- epoch errado;
- action desconhecida;
- budget acima de 128;
- expiracao longa demais;
- policy ja expirada;
- accepted e depois expirada antes do workload.

Foram executados 20 repeticoes por caso em WARP Release e Debug: 320/320
processos passaram, com evidencia normalizada deterministica entre repeticoes e
configuracoes.

### Interferencia sustentada

O comando `sustained-copy-lab` cria buffers owned de 4 MiB, executa uma copia
necessaria e 128 repeticoes sem alteracao. Baseline e optimized rodam em
processos separados, com ordem alternada.

No budget padrao de 128:

- baseline observa/encaminha 135 `CopyResource`;
- optimized observa 135, encaminha 7 e elimina 128;
- 536.870.912 bytes logicos deixam de ser copiados por run otimizado;
- hashes FNV-1a de origem/destino continuam identicos;
- conteudo legado, subresource, eventos, snapshot e rollback continuam exatos;
- nenhum evento foi perdido e nenhum overrun ocorreu.

### Evidencia de performance

RX 580, 1 warmup + 10 pares medidos:

- GPU wins: 10/10;
- GPU p50: 21.487,600 us -> 314,960 us;
- GPU p95: 27.472,856 us -> 356,784 us;
- CPU p50: 8.322,450 us -> 8.477,450 us;
- CPU paired p95 delta: +885,810 us.

O gate positivo passou somente para:

`owned-d3d11-sustained-copy-elision-gpu-workload-only`

A regressao pequena de CPU fica registrada. Nao extrapolar para FPS, frame time,
jogo externo, potencia, RAM/VRAM ou eficiencia geral.

## 5. ABIs e invariantes

- Snapshot ABI: 9
- Attach-options ABI: 2
- Ring ABI: 6
- Control ABI: 1
- Event size: 80 bytes
- Ring capacity: 1024
- Ring header: 64 bytes
- Control block: 64 bytes
- Mapping total: 82048 bytes
- `ControlPolicyAccepted = 15`

Invariantes da policy:

- target owned e opt-in obrigatorios;
- attach-option skip e managed policy sao mutuamente exclusivos;
- epoch 1 e action mask 1;
- budget entre 1 e 128;
- expiracao futura e no maximo 4 segundos;
- reserva atomica impede ultrapassar o budget;
- policy invalida/expirada falha fechada;
- detach desativa atuacao e restaura dispatch;
- o modulo permanece pinado ate o processo terminar.

## 6. Como verificar

```powershell
dotnet test FluidRuntime.slnx -c Release
dotnet build FluidRuntime.slnx -c Release

cmake -S native -B native/build -A x64
cmake --build native/build --config Release
cmake --build native/build --config Debug
ctest --test-dir native/build -C Release --output-on-failure
ctest --test-dir native/build -C Debug --output-on-failure

dotnet run --project src/FluidRuntime -c Release -- sustained-copy-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --copy-count 128 --trial-pairs 10 --warmup-pairs 1 `
  --hold-ms 50 --gpu-timeout-ms 5000 --hardware true `
  --out artifacts/sustained-copy-hardware.json
```

## 7. Evidencia

- [v0.9.0 report](evidence/v0.9.0-sustained-copy-elision.md)
- [policy matrix trace](evidence/traces/control-policy-matrix-v0.9.0.json)
- [WARP trace](evidence/traces/sustained-copy-warp-v0.9.0.json)
- [RX 580 trace](evidence/traces/sustained-copy-rx580-v0.9.0.json)
- [architecture](architecture.md)
- [roadmap](roadmap.md)

## 8. Proximo passo recomendado

Antes de qualquer external attach, ampliar a prova de proveniencia para aliases,
shader draw/dispatch writes, fences, deferred contexts e sincronizacao. Em
paralelo, o FluidGateway deve passar a gerar shadow policies a partir do ledger,
sem autorizar atuacao ate os gates de identidade, regressao e rollback passarem.

Depois disso, a progressao segura e:

1. observacao externa allowlisted e read-only;
2. policy shadow em alvo autorizado;
3. primeira atuacao externa opt-in com rollback;
4. backend separado para CPU scheduling;
5. backend separado para RAM/VRAM residency;
6. D3D12 e Vulkan.

Mensagem curta para o proximo agente: a v0.9.0 prova interferencia sustentada e
ganho GPU apenas no lab D3D11 owned. Nao alargue o claim. Preserve os ABIs,
complete proveniencia/sincronizacao e exija evidencia pareada antes de promover
qualquer novo backend.
