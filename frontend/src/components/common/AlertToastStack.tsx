import { useAlertNotifications } from '../../hooks/useAlertNotifications'
import '../../styles/alert-toast-stack.css'

export default function AlertToastStack() {
  const { alerts, dismiss } = useAlertNotifications()

  if (alerts.length === 0) return null

  return (
    <div className="alert-toast-stack" role="region" aria-label="Alert notifications">
      {alerts.map(alert => (
        <div key={alert.id} className={`alert-toast alert-toast--${alert.severity.toLowerCase()}`} role="alert">
          <div className="alert-toast__content">
            <div className="alert-toast__title">{alert.title}</div>
            <div className="alert-toast__body">{alert.body}</div>
          </div>
          <button
            className="alert-toast__dismiss"
            aria-label="Dismiss notification"
            onClick={() => dismiss(alert.id)}
          >
            ×
          </button>
        </div>
      ))}
    </div>
  )
}
