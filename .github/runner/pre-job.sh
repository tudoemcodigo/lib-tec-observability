#!/usr/bin/env bash
# Hook de início de job do runner self-hosted com identidade no Azure (integracao-azure.yml).
#
# Por que existe: o repositório é público. Num PR de fork o GitHub executa os workflows do commit do PR, então um fork pode
# trazer o PRÓPRIO workflow com runs-on: [self-hosted, azure] e sem as condições do integracao-azure.yml. O hook roda no runner
# antes de qualquer passo e recusa (exit 1, job falha sem executar nada) todo job que não seja o integracao-azure.yml da main
# deste repositório, disparado por schedule ou workflow_dispatch.
#
# Instalação (uma vez, na imagem do runner; ver .github/workflows/README.md):
#   1. Copie este arquivo para FORA do diretório de trabalho do runner (ex.: /opt/actions-runner-hooks/pre-job.sh), com dono
#      root e sem permissão de escrita para o usuário do runner. Nunca aponte o hook para o checkout: lá está o código do PR.
#   2. No arquivo .env do runner: ACTIONS_RUNNER_HOOK_JOB_STARTED=/opt/actions-runner-hooks/pre-job.sh
#
# Não substitui as configurações do GitHub (aprovação de colaboradores externos, runner group restrito, runner efêmero): soma-se
# a elas.
set -euo pipefail

readonly REPOSITORY="tudoemcodigo/lib-tec-observability"
readonly WORKFLOW_REF="${REPOSITORY}/.github/workflows/integracao-azure.yml@refs/heads/main"

refuse() {
  echo "::error::Runner com identidade no Azure: job recusado ($1)." >&2
  exit 1
}

case "${GITHUB_EVENT_NAME:-}" in
  schedule | workflow_dispatch) ;;
  *) refuse "evento '${GITHUB_EVENT_NAME:-?}'; só schedule e workflow_dispatch" ;;
esac

[[ "${GITHUB_REPOSITORY:-}" == "$REPOSITORY" ]] || refuse "repositório '${GITHUB_REPOSITORY:-?}'"
[[ "${GITHUB_REF:-}" == "refs/heads/main" ]] || refuse "ref '${GITHUB_REF:-?}'; só a main"
[[ "${GITHUB_WORKFLOW_REF:-}" == "$WORKFLOW_REF" ]] || refuse "workflow '${GITHUB_WORKFLOW_REF:-?}'"

echo "Job autorizado no runner com identidade no Azure: ${GITHUB_WORKFLOW_REF} (${GITHUB_EVENT_NAME})."
