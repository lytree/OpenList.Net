import { computed, defineComponent, ref, type PropType } from "vue"
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElRadio,
  ElRadioGroup,
  ElScrollbar,
  ElTag,
} from "element-plus"
import { fsBatchRename } from "~/api/fs"
import type { Obj } from "~/api/types"
import {
  planFindReplace,
  planRegexRename,
  planSequentialRename,
  type RenameMode,
  type RenamePlan,
} from "~/utils/rename"

/**
 * 批量重命名（正则替换 / 序号命名 / 查找替换）。
 *
 * 与上游的差别：**规则表单与预览是同一个对话框**。上游是两个 Modal 串起来，
 * 改一个字符就得「上一步」重来；这里改成实时预览 —— 输入即算，问题（非法名/撞名）
 * 在提交前就以红色标出，不用等后端 500。
 */
export default defineComponent({
  name: "BatchRenameDialog",
  props: {
    visible: { type: Boolean, default: false },
    dir: { type: String, required: true },
    targets: { type: Array as PropType<Obj[]>, default: () => [] },
  },
  emits: {
    "update:visible": (_visible: boolean) => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const mode = ref<RenameMode>("regex")
    /** 正则模式：pattern + replacement；查找替换模式：find + replace。 */
    const first = ref("")
    const second = ref("")
    /** 序号模式：模板 + 起始序号 + 补零位数。 */
    const template = ref("{number}")
    const start = ref(1)
    const padding = ref(0)
    const saving = ref(false)

    const sources = computed(() =>
      props.targets.map((obj) => ({
        name: obj.name,
        is_dir: obj.is_dir,
        created: obj.created,
        modified: obj.modified,
      })),
    )

    const plan = computed<RenamePlan>(() => {
      if (mode.value === "regex") {
        // 空正则会把每个字符之间都匹配一遍，没有意义，按「无变化」处理
        if (!first.value) return { items: noChange(sources.value), changed: [], problems: [] }
        return planRegexRename(sources.value, first.value, second.value)
      }
      if (mode.value === "sequence") {
        if (!template.value.trim()) return { items: noChange(sources.value), changed: [], problems: [] }
        return planSequentialRename(sources.value, template.value, start.value, padding.value)
      }
      if (!first.value) return { items: noChange(sources.value), changed: [], problems: [] }
      return planFindReplace(sources.value, first.value, second.value)
    })

    /** 未配置规则时的占位计划（保持原名、不提交）。 */
    function noChange(list: { name: string }[]) {
      return list.map((item) => ({ src_name: item.name, new_name: item.name, unchanged: true }))
    }

    const canSubmit = computed(() => plan.value.changed.length > 0 && plan.value.problems.length === 0)

    function switchMode(next: string | number | boolean | undefined) {
      mode.value = String(next) as RenameMode
      // 三个模式的「第一个输入框」语义完全不同，切模式时清空避免误用
      first.value = ""
      second.value = ""
      if (mode.value === "sequence") {
        template.value = "{number}"
        start.value = 1
        padding.value = 0
      }
    }

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      if (!canSubmit.value) return
      saving.value = true
      try {
        const resp = await fsBatchRename(
          props.dir,
          plan.value.changed.map((item) => ({ src_name: item.src_name, new_name: item.new_name })),
        )
        if (resp.code === 200) {
          ElMessage.success(`已重命名 ${plan.value.changed.length} 项`)
          first.value = ""
          second.value = ""
          emit("done")
        } else {
          ElMessage.error(resp.message || `HTTP ${resp.code}`)
        }
      } catch (e) {
        ElMessage.error(String((e as Error)?.message ?? e))
      } finally {
        saving.value = false
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title={`批量重命名（${props.targets.length} 项）`}
        width="760px"
        onUpdate:modelValue={(value: boolean) => emit("update:visible", value)}
        v-slots={{
          default: () => (
        <div class="ol-dialog-col">
          <ElRadioGroup modelValue={mode.value} onUpdate:modelValue={switchMode}>
            <ElRadio value="regex">正则替换</ElRadio>
            <ElRadio value="sequence">序号命名</ElRadio>
            <ElRadio value="replace">查找替换</ElRadio>
          </ElRadioGroup>

          {mode.value === "regex" && (
            <>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">正则表达式</span>
                <ElInput
                  modelValue={first.value}
                  placeholder="如 ^IMG_(\d+)"
                  onUpdate:modelValue={(value: string) => (first.value = value)}
                />
              </div>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">替换为（支持 $1 反向引用，以及 created_year 这类时间变量）</span>
                <ElInput
                  modelValue={second.value}
                  placeholder="如 photo-$1"
                  onUpdate:modelValue={(value: string) => (second.value = value)}
                />
              </div>
            </>
          )}

          {mode.value === "sequence" && (
            <>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">文件名模板（number 占位符为序号，ext 占位符为原扩展名）</span>
                <ElInput
                  modelValue={template.value}
                  placeholder="如 ep{number}"
                  onUpdate:modelValue={(value: string) => (template.value = value)}
                />
              </div>
              <div class="ol-dialog-row">
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">起始序号</span>
                  <ElInputNumber
                    modelValue={start.value}
                    min={1}
                    step={1}
                    controlsPosition="right"
                    onUpdate:modelValue={(value: number | undefined) => (start.value = value ?? 1)}
                  />
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">补零位数（0 = 不补）</span>
                  <ElInputNumber
                    modelValue={padding.value}
                    min={0}
                    max={10}
                    step={1}
                    controlsPosition="right"
                    onUpdate:modelValue={(value: number | undefined) => (padding.value = value ?? 0)}
                  />
                </div>
              </div>
            </>
          )}

          {mode.value === "replace" && (
            <>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">查找（区分大小写，替换全部出现位置）</span>
                <ElInput
                  modelValue={first.value}
                  placeholder="如 副本"
                  onUpdate:modelValue={(value: string) => (first.value = value)}
                />
              </div>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">替换为（留空即删除）</span>
                <ElInput
                  modelValue={second.value}
                  onUpdate:modelValue={(value: string) => (second.value = value)}
                />
              </div>
            </>
          )}

          {plan.value.problems.length > 0 && (
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={plan.value.problems.join("；")}
            />
          )}

          <div class="ol-rename-preview">
            <div class="ol-rename-head">
              <span>原文件名</span>
              <span>新文件名</span>
            </div>
            <ElScrollbar maxHeight="280px">
              {plan.value.items.map((item) => (
                <div
                  class={item.error ? "ol-rename-row is-error" : item.unchanged ? "ol-rename-row is-same" : "ol-rename-row"}
                  key={item.src_name}
                >
                  <span class="ol-rename-name" title={item.src_name}>
                    {item.src_name}
                  </span>
                  <span class="ol-rename-name" title={item.new_name}>
                    {item.unchanged ? "（不变）" : item.new_name}
                  </span>
                </div>
              ))}
            </ElScrollbar>
          </div>

          <div class="ol-rename-summary">
            <ElTag size="small" type={canSubmit.value ? "success" : "info"}>
              将重命名 {plan.value.changed.length} 项
            </ElTag>
            <span class="ol-preview-hint">
              共 {plan.value.items.length} 项，{plan.value.items.length - plan.value.changed.length} 项保持不变
            </span>
          </div>
        </div>
          ),
          footer: () => (
            <div class="ol-dialog-footer">
              <ElButton onClick={close}>取消</ElButton>
              <ElButton type="primary" loading={saving.value} disabled={!canSubmit.value} onClick={submit}>
                应用
              </ElButton>
            </div>
          ),
        }}
      />
    )
  },
})
