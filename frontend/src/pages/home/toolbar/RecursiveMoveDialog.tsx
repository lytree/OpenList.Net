import { computed, defineComponent, ref, watch, type PropType } from "vue"
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElMessage,
  ElOption,
  ElSelect,
} from "element-plus"
import { fsRecursiveMove } from "~/api/fs"
import FolderPickerDialog from "~/components/FolderPickerDialog"
import { pathJoin } from "~/utils/path"
import type { Obj } from "~/api/types"

/**
 * 递归移动：把**整个选中目录**（含所有子目录）搬到目标目录。
 *
 * 为什么不复用「移动到」：`/fs/move` 是「在同一层按名字搬 N 项」，本质是 rename；
 * `/fs/recursive_move` 是跨存储/跨层级的整树复制+删除语义，服务端会自己递归。
 * 上游把两者放在同一个 CopyMove 组件里靠 `recursive` 开关切换，我们拆成两个入口，
 * 避免「同样的按钮点下去行为取决于某个看不见的开关」。
 */
export default defineComponent({
  name: "RecursiveMoveDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    /** 只接受单个目录（调用方保证）。 */
    target: { type: Object as PropType<Obj | null>, default: null },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const dstDir = ref("")
    const policy = ref(0)
    const busy = ref(false)
    const pickerVisible = ref(false)

    const srcPath = computed(() =>
      props.target ? pathJoin(props.dir, props.target.name) : "",
    )

    watch(
      () => props.visible,
      (open) => {
        if (open) {
          dstDir.value = ""
          policy.value = 0
        }
      },
    )

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      if (!props.target) return
      if (!dstDir.value) {
        ElMessage.warning("请先选择目标目录")
        return
      }
      if (dstDir.value === props.dir) {
        ElMessage.warning("目标目录不能是当前目录")
        return
      }
      busy.value = true
      try {
        const resp = await fsRecursiveMove(srcPath.value, dstDir.value, policy.value)
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "移动失败")
          return
        }
        ElMessage.success("已移动")
        emit("done")
        close()
      } finally {
        busy.value = false
      }
    }

    return () => (
      <>
        <ElDialog
          modelValue={props.visible}
          title="递归移动"
          width="520px"
          onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
          v-slots={{
            default: () => (
              <div class="ol-dialog-col">
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">源目录</span>
                  <span class="ol-dialog-path">{srcPath.value || "-"}</span>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">目标目录</span>
                  <div class="ol-dialog-row">
                    <span class="ol-dialog-path">{dstDir.value || "未选择"}</span>
                    <ElButton size="small" onClick={() => (pickerVisible.value = true)}>
                      选择
                    </ElButton>
                  </div>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">冲突处理</span>
                  <ElSelect
                    modelValue={policy.value}
                    size="small"
                    style={{ width: "200px" }}
                    onUpdate:modelValue={(v: number) => (policy.value = Number(v))}
                  >
                    <ElOption label="跳过已存在的文件" value={0} />
                    <ElOption label="覆盖已存在的文件" value={1} />
                  </ElSelect>
                </div>
                <ElAlert
                  type="warning"
                  showIcon={true}
                  closable={false}
                  title="整棵树会被搬到目标目录，源目录随后被移除。大目录耗时较长，请勿关闭页面。"
                />
              </div>
            ),
            footer: () => (
              <div class="ol-dialog-footer">
                <ElButton onClick={close}>取消</ElButton>
                <ElButton type="primary" loading={busy.value} onClick={submit}>
                  开始移动
                </ElButton>
              </div>
            ),
          }}
        />
        <FolderPickerDialog
          visible={pickerVisible.value}
          title="选择目标目录"
          initialDir={props.dir}
          onUpdate:visible={(v: boolean) => (pickerVisible.value = v)}
          onChosen={(dir: string) => {
            dstDir.value = dir
            pickerVisible.value = false
          }}
        />
      </>
    )
  },
})
