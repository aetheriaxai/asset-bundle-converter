import { future } from 'fp-future'
import { spawn } from 'child_process'
import { ILoggerComponent } from '@well-known-components/interfaces'

export function execCommand(logger: ILoggerComponent.ILogger, command: string, args: string[], env: Record<string, string>, cwd: string, timeout: number | undefined) {
  const exitFuture = future<number | null>()

  const child = spawn(command, args, { env, cwd, timeout })
    .on('exit', (code, signal) => {
      if (signal === 'SIGTERM' || signal == 'SIGKILL') {
        exitFuture.reject(new Error('SIGTERM sent to the process'))
      } else {
        exitFuture.resolve(code ?? -1)
      }
    })
    .on('error', (error) => {
      logger.error(error)
      exitFuture.reject(error)
    })

  if (timeout) {
    setTimeout(() => {
      try {
        if (!child.killed) {
          logger.warn('Process did not finish, killing process', { pid: child.pid?.toString() || '?' })
          child.kill('SIGKILL')
        }
      } catch { }
    }, timeout + 1000)
  }

  child.stdout?.on('data', (data) => {
    logger.log(data)
  })

  child.stderr?.on('data', (data) =>
    logger.error(data)
  )

  return { exitPromise: exitFuture, child }
}
