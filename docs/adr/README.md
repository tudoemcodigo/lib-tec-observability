<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

[🏠 TEC.Observability](../../README.md) › [📚 Documentação](../README.md) › 🏛️ Decisões de arquitetura (ADRs)

# 🏛️ Registros de Decisões de Arquitetura (ADR) — TEC.Observability

Este diretório contém os Registros de Decisões de Arquitetura (**Architecture Decision Records — ADRs**) do componente **TEC.Observability**.

O objetivo destes registros é documentar de forma clara, contextualizada e justificada as escolhas técnicas, de engenharia de software e de segurança adotadas nos pacotes `TEC.Observability` e `TEC.Observability.Azure`, orientando a manutenção, a evolução e a auditoria contínua do componente.

> [!NOTE]
> Os ADRs são **retroativos**: foram escritos em 2026-10-24 a partir do código da versão 0.0.1, da [revisão técnica](../revisao-tecnica.md) de 2026-10-19 e da documentação. A data de cada registro é a do registro, não a da decisão. Uma decisão que mudar ganha um ADR novo que substitui o anterior (o anterior passa a `Substituído`), em vez de ser reescrita.

## 📑 Sumário

- [📑 Índice de Decisões](#-índice-de-decisões)
- [🧭 Como ler e como propor um ADR](#-como-ler-e-como-propor-um-adr)

---

## 📑 Índice de Decisões

| ADR | Título | Status | Data | Contexto Resumido |
|:---:|:---|:---:|:---:|:---|
| [0001](0001-azure-monitor-em-pacote-separado-com-credencial-restrita.md) | **Azure Monitor em Pacote Separado, com Credencial Restrita Fora de Desenvolvimento** | Aprovado | 2026-10-24 | O pacote base fica sem SDKs de nuvem e compatível com Native AOT; `TEC.Observability.Azure` traz a distro, só liga com `Azure` no `Provider` e fora de `Development` aceita apenas Workload/Managed Identity. |
| [0002](0002-exportadores-plugaveis-escolhidos-por-nome-no-provider.md) | **Exportadores Plugáveis Escolhidos por Nome em `Observability:Provider`** | Aprovado | 2026-10-24 | `ITelemetryExporter` e destinos combináveis (`"Otlp, LogFile"`); destino sem exportador, OTLP sem TLS com cabeçalhos e nomes inválidos derrubam a subida. |
| [0003](0003-redacao-de-dados-sensiveis-antes-de-qualquer-exportador.md) | **Redação de Dados Sensíveis Antes de Qualquer Exportador** | Aprovado | 2026-10-24 | Heurística linear, sem regex e sem reflexão, que prefere esconder a vazar; processadores no início da coleção, `RedactedException` e `RedactAllLoggerProviders` opcional. |
| [0004](0004-correlation-id-no-trace-w3c-e-baggage-restrito-por-host.md) | **Correlation ID Ligado ao Trace W3C e Baggage Restrito por Host** | Aprovado | 2026-10-24 | `X-Correlation-ID` validado, Baggage de entrada descartado, Baggage de saída só para `BaggageAllowedHosts` nos dois propagadores e propagação mesmo com `Provider: None`. |
| [0005](0005-health-checks-liveness-readiness-com-execucao-unica-cache-e-prazo.md) | **Health Checks com Liveness e Readiness Separados, Execução Única, Cache e Prazo** | Aprovado | 2026-10-24 | Rajada de sondas não vira carga nas dependências: execução compartilhada, cache, prazo global, teto de execuções abandonadas e orçamento conferido na subida. |
| [0006](0006-exposicao-minima-dos-endpoints-de-health-e-porta-de-gerencia.md) | **Exposição Mínima dos Endpoints de Health e Porta de Gerência** | Aprovado | 2026-10-24 | JSON mínimo fora de `Development`; detalhes só com `ManagementPort` conferida na conexão (o cabeçalho `Host` não é barreira). |
| [0007](0007-configuracao-validada-na-subida-e-lida-na-criacao-dos-componentes.md) | **Configuração Validada na Subida e Lida na Criação dos Componentes** | Aprovado | 2026-10-24 | `ValidateOnStart` com todos os problemas numa mensagem sem repetir valores; provedores de configuração tardios (cofre) valem, exceto para o `Provider`. |
| [0008](0008-integracao-sem-acoplamento-com-componentes-tec-e-aspire.md) | **Integração sem Acoplamento com os Componentes TEC e Convivência com o .NET Aspire** | Aprovado | 2026-10-24 | Assinatura das fontes `TEC.*` por nome, sem dependência entre pacotes; liveness `self` reaproveitado e um único exportador com o `ServiceDefaults`. |

---

## 🧭 Como ler e como propor um ADR

| Tópico | Regra |
|---|---|
| Estrutura | `Status`, `Data`, `Contexto`, `Decisão` e `Consequências` (positivas e neutras/negativas), como nos ADRs do TEC.Vault e do TEC.Core |
| Nome do arquivo | `NNNN-titulo-em-portugues.md`, numeração sequencial sem reaproveitar números |
| Status | `Aprovado`, `Substituído` (com link para o ADR novo) ou `Obsoleto` |
| Evidência | Cada decisão aponta o código (links relativos) e o documento de uso; nada é registrado sem evidência no repositório |
| Quando propor | Mudança de contrato público, de padrão de segurança, de dependência ou de comportamento padrão |

---
⬅️ [📚 Índice](../README.md) · [Revisão técnica](../revisao-tecnica.md)
