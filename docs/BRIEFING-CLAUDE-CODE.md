# Briefing FluidRuntime / FluidGateway

Handoff atualizado em 2026-07-26 para o release candidate v0.11.0.

## 1. Objetivo geral

Construir um runtime open source que reduza desperdicio entre CPU, GPU, RAM,
VRAM, recursos graficos e apresentacao. A tese e:

> O futuro da performance nao e so mais potencia. E menos desperdicio.

Nao e clone de DLSS, FSR ou Lossless Scaling. Software nao transforma hardware
discreto em memoria fisicamente unificada, mas pode observar as APIs expostas,
tomar decisoes antes, evitar trabalho redundante e reverter atuacao quando a
evidencia falha.

## 2. Repositorios

| Repo | Papel |
| --- | --- |
| [FluidGateway](https://github.com/maxhuntert1414-max/FluidGateway) | PresentMon, diagnostico, evidencia, policy e ledger operacional |
| [FluidRuntime](https://github.com/maxhuntert1414-max/FluidRuntime) | Telemetria Windows/GPU, hook D3D11 cooperativo, control plane, atuacao owned e rollback |

Fluxo atual:

```text
PresentMon CSV
  -> FluidGateway (achados + ledger)
  -> FluidRuntime (.NET manager + telemetria)
  -> shared-memory control block
  -> native D3D11 hook (target owned com opt-in)
```

## 3. Nivel de operacao

Ja e real:

- diagnostico PresentMon offline em HTML/JSON;
- probe read-only de processo, RAM, GPU e VRAM pelo Windows;
- observacao cooperativa D3D11 de Present, recursos, lifecycle, maps, updates,
  copies, subresources e RTV/UAV clears;
- ring de memoria compartilhada com validacao nativa/managed e perda zero;
- policy managed de um epoch, uma action exata e budget de 1..128;
- elisao reversivel de `CopyResource` redundante no lab owned;
- elisao dedicada de readback `DEFAULT -> STAGING + CPU_READ` no lab owned;
- elisao dedicada de upload `STAGING + CPU_WRITE -> DEFAULT` no lab owned;
- baseline/optimized pareado, hashes, adapter identity, timing e rollback.

Ainda nao e real:

- injection/attach em jogos ou processos externos;
- scheduler de threads do Windows;
- controle de residencia fisica RAM/VRAM;
- upload dinamico, `UpdateSubresource`, batching ou residencia fisica;
- atuacao no presentation path;
- D3D12 ou Vulkan;
- claim geral de FPS, energia ou "salvar maquina velha".

## 4. Entregas v0.10.0 e v0.11.0

### Contrato de readback

O hook registra `D3D11_USAGE` e `CPUAccessFlags` apenas para recursos cuja
criacao e lifetime foram observados. Uma copia e classificada como readback
somente quando:

- origem: `D3D11_USAGE_DEFAULT`;
- destino: `D3D11_USAGE_STAGING`;
- destino: `D3D11_CPU_ACCESS_READ`;
- proveniencia de ambos continua confiavel;
- a repeticao usa a mesma origem/geracao e o destino nao mudou.

Action 1 continua sendo copia generica. Action 2 e exclusiva para readback.
Uma mask combinada ou desconhecida e rejeitada.

### Workload owned

`readback-elision-lab` cria um buffer de origem de 4 MiB e um staging legivel.
Cada processo executa uma copia/map necessaria e 64 repeticoes inalteradas.

- baseline: 65 readback copies encaminhadas, 65 maps;
- optimized: 1 readback copy encaminhada, 64 puladas, 65 maps;
- economia logica por run otimizado: 268.435.456 bytes;
- todos os 4 MiB sao comparados depois de cada map;
- expected, first-map, final-map, source e destination hashes devem coincidir;
- snapshot, eventos, policy e rollback devem coincidir sem perda.

### Transporte e observabilidade

O novo `MapRead` levou a carga acima do ring antigo. O primeiro baseline
registrou 18 overruns e falhou corretamente. O ring ABI 7 foi ampliado para
2.048 eventos e o gate continua exigindo zero overrun e zero sequencia perdida.

### Contrato de upload v0.11

Uma copia e classificada como upload somente quando:

- origem: `D3D11_USAGE_STAGING` com `D3D11_CPU_ACCESS_WRITE`;
- destino: `D3D11_USAGE_DEFAULT`;
- ambos foram observados na criacao e continuam com proveniencia confiavel;
- a mesma origem/geracao ja foi copiada e o destino nao mudou.

O target escreve 4 MiB uma vez via `Map`/`Unmap`, encaminha uma copia
obrigatoria e repete a copia 64 vezes. Action bit 4 e exclusiva para upload.
Um novo `Unmap` de escrita muda a geracao da origem e obriga a proxima copia a
ser encaminhada. Um skip nao muda geracao porque nenhum byte foi escrito.

## 5. Evidencia local v0.11

Validacao concluida:

- managed tests: 73/73;
- CTests Release: 8/8;
- CTests Debug: 8/8;
- matriz negativa Release/Debug: 320/320;
- WARP upload: 4/4 raw runs, claim bloqueado;
- RX 580 upload: 22/22 raw runs, 1 warmup + 10 pares medidos.

AMD Radeon RX 580 2048SP:

| Metrica | Baseline p50 | Optimized p50 | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: |
| CPU submission QPC | 11.989,250 us | 11.987,300 us | 13.397,080 us | 12.391,395 us |
| GPU timestamp interval | 31.883,320 us | 1.669,600 us | 32.520,304 us | 1.734,448 us |

GPU wins: 10/10. CPU wins: 6/10, com 10/10 pares dentro do envelope
predeclarado de +1.000 us / +10%. Delta pareado GPU p50/p95: -94,814% e
-94,445%. Delta CPU p95: +377,070 us (+3,198%). O gate passou somente para:

`owned-d3d11-writable-staging-to-default-upload-copy-workload-only`

Base do claim:

`gpu-interval-improvement-with-bounded-cpu-submission-overhead`

O GPU timestamp mede o intervalo entre comandos D3D11, nao GPU busy por hardware
counter. Nao extrapolar para PCIe, residencia fisica, FPS, energia ou jogo; nao
afirmar que CPU ficou mais rapida.

## 6. ABIs e invariantes

- Snapshot ABI: 11
- Attach-options ABI: 2
- Ring ABI: 8
- Control ABI: 1
- Event size: 80 bytes
- Ring capacity: 2.048
- Ring header: 64 bytes
- Control block: 64 bytes
- Mapping total: 163.968 bytes
- `ControlPolicyAccepted = 15`
- `MapRead = 16`
- action generic copy: 1
- action readback copy: 2
- action upload copy: 4

Invariantes:

- target owned e opt-in obrigatorios;
- attach-option e managed policy sao mutuamente exclusivos;
- epoch unico, action exata, budget 1..128;
- expiracao futura e no maximo 4 segundos;
- reserva atomica nao ultrapassa budget;
- policy invalida/expirada falha fechada;
- detach desativa atuacao e restaura dispatch;
- modulo permanece pinado ate o processo terminar;
- reattach no mesmo processo e rejeitado.

## 7. Como verificar

```powershell
dotnet test FluidRuntime.slnx -c Release

cmake -S native -B native/build -A x64
cmake --build native/build --config Release
cmake --build native/build --config Debug
ctest --test-dir native/build -C Release --output-on-failure
ctest --test-dir native/build -C Debug --output-on-failure

dotnet run --project src/FluidRuntime -c Release -- upload-elision-lab `
  --target native/build/Release/fluidruntime-hook-target.exe `
  --hook native/build/Release/fluidruntime-present-hook.dll `
  --trial-pairs 10 --warmup-pairs 1 `
  --hold-ms 50 --gpu-timeout-ms 5000 --hardware true `
  --out artifacts/upload-elision-hardware.json
```

## 8. Arquivos centrais

- Native API: `native/include/fluidruntime_hook_api.h`
- Hook: `native/src/present_hook.cpp`
- Target: `native/src/hook_target.cpp`
- Managed ring/policy: `src/FluidRuntime/Native/HookRingReader.cs`
- Upload runner: `src/FluidRuntime/Runtime/UploadElisionLabRunner.cs`
- Claim report: `src/FluidRuntime/Runtime/UploadElisionLabReport.cs`
- CI: `.github/workflows/ci.yml`

## 9. Evidencia

- [v0.11.0 report](evidence/v0.11.0-upload-elision.md)
- [RX 580 upload trace](evidence/traces/upload-elision-rx580-v0.11.0.json)
- [WARP upload trace](evidence/traces/upload-elision-warp-v0.11.0.json)
- [policy matrix v0.11](evidence/traces/control-policy-matrix-v0.11.0.json)
- [v0.10.0 report](evidence/v0.10.0-readback-elision.md)
- [RX 580 trace](evidence/traces/readback-elision-rx580-v0.10.0.json)
- [WARP trace](evidence/traces/readback-elision-warp-v0.10.0.json)
- [policy matrix](evidence/traces/control-policy-matrix-v0.10.0.json)
- [architecture](architecture.md)
- [roadmap](roadmap.md)

## 10. Proximo passo recomendado

O proximo passo e ampliar a proveniencia do upload para `UpdateSubresource`,
buffers dynamic, regioes parciais, reuse, batching, fences e sincronizacao. Cada
novo padrao precisa de action separada, equivalencia e rollback; residencia
fisica continua bloqueada.

Em paralelo, endurecer aliases, shader writes, fences, deferred contexts e
command lists. External attach continua depois desses contratos, com allowlist,
consentimento, modo read-only e rollback.

Mensagem curta: v0.11 prova interferencia especifica no upload D3D11 owned,
com intervalo GPU menor, overhead CPU limitado e conteudo identico na RX 580.
Nao alargue o claim e nao chame `DEFAULT` de VRAM fisica nem `STAGING` de RAM
fisica.
