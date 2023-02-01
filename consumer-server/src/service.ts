import { ILoggerComponent, Lifecycle } from "@well-known-components/interfaces"
import { setupRouter } from "./controllers/routes"
import { executeConversion } from "./logic/conversion-task"
import disk from 'diskusage'
import { AppComponents, GlobalContext, TestComponents } from "./types"

// this function wires the business logic (adapters & controllers) with the components (ports)
export async function main(program: Lifecycle.EntryPointParameters<AppComponents | TestComponents>) {
  const { components, startComponents } = program
  const globalContext: GlobalContext = {
    components,
  }

  // wire the HTTP router (make it automatic? TBD)
  const router = await setupRouter(globalContext)
  // register routes middleware
  components.server.use(router.middleware())
  // register not implemented/method not allowed/cors responses middleware
  components.server.use(router.allowedMethods())
  // set the context to be passed to the handlers
  components.server.setContext(globalContext)

  // start ports: db, listeners, synchronizations, etc
  await startComponents()

  const logger = components.logs.getLogger('main-loop')

  components.runner.runTask(async (opt) => {
    while (opt.isRunning) {
      if (await machineRanOutOfSpace(components)) {
        logger.warn('Stopping program due to lack of disk space')
        void program.stop()
        return
      }

      await components.taskQueue.consumeAndProcessJob(async (job, message) => {
        await executeConversion(components, job.entity.entityId, job.contentServerUrls![0])
      })
    }
  })
}

async function machineRanOutOfSpace(components: Pick<AppComponents, 'metrics'>) {
  const diskUsage = await disk.check('/')
  const free = diskUsage.free

  components.metrics.observe('ab_converter_free_disk_space', {}, free)

  if (free / 1e9 < 2 /* less than 2gb */) {
    return true
  }

  return false
}

async function timeout(ms: number, message: string) {
  return new Promise<never>((_, reject) => setTimeout(() => reject(new Error(message)), ms))
}

