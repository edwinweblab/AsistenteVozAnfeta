![ANFETA Resumen de Actualización](C:/Users/nanoc/.gemini/antigravity-ide/brain/30e0fb51-cf13-4ed0-a84d-57dd90aee071/infografia_avances_anfeta_1790561910851.jpg)

---

## 📌 Resumen de Cambios Implementados

| # | Módulo | Cambio Principal | Estado |
| :---: | :--- | :--- | :---: |
| **1** | **Modal `Ctrl + V`** | Selector dual **Notion** vs **Dropbox** con detección de URLs/texto, nombre editable, disclaimer de carpeta `RX/{dominio}` y actualización dinámica de textos. | ✅ Listo |
| **2** | **Ventana Fijada** | Layout estructurado en **2 columnas**: Editor completo a la izquierda y actividades agrupadas a la derecha con `ScrollViewer` independiente en ambas columnas. | ✅ Listo |
| **3** | **Escala de UI** | Escala visual adaptativa según monitor: **1.02x** en 1080p (cero cortes ni saltos de línea), **1.16x** en 1440p y **1.38x** en 4K. | ✅ Listo |
| **4** | **Plantillas Fase 1** | Catálogo en 2 filas con botones tipo chip: Cobros (`prtuzCOBRAR`, `prtuzPAGAR`), Revisiones y Redes Sociales estilizadas. | ✅ Listo |
| **5** | **Avance Diario** | Rediseño de tarjetas de auditoría y regla de negocio: exclusión de actividades **FTF** de las métricas de revisión. | ✅ Listo |
| **6** | **Sync y Rendimiento** | Botón de sincronización con Notion en modo solo icono para eliminar scroll lateral horizontal. | ✅ Listo |

---

## 🔍 Guía de Uso y Verificación Paso a Paso

### 1️⃣ Modal de Pegado Global (`Ctrl + V`)

#### ¿Qué hace?
Permite enviar texto o URLs del portapapeles a dos destinos distintos sin salir de ANFETA:
- **Notion · Revisiones**: Crea la actividad directamente en la base de datos de Revisiones de Notion.
- **Dropbox · RX/{dominio}**: Guarda un archivo `.txt` (si es texto) o un acceso directo `.url` (si es enlace web) en la carpeta local `RX/{dominio}/`.

#### ¿Cómo verificarlo?
1. Copia un enlace (ej. `https://ejemplo.com`) o un texto al portapapeles.
2. En ANFETA presiona <kbd>Ctrl</kbd> + <kbd>V</kbd>.
3. **Alterna entre los RadioButtons**:
   - Al marcar **🌐 Notion · Revisiones**:
     - El título muestra: `📋 Pegar texto en Notion · Revisiones`.
     - El campo de título indica: `Título de la nueva página en Notion:`.
     - El botón inferior muestra: `Crear actividad`.
   - Al marcar **📦 Dropbox · RX/{dominio}**:
     - El título cambia automáticamente a: `🔗 Guardar enlace en Dropbox · RX/{dominio}` o `📄 Guardar texto en Dropbox · RX/{dominio}`.
     - Aparece el cuadro de aviso: *📁 Aviso de Dropbox · Creación de carpeta RX/{dominio}*.
     - El campo de título cambia a: `Nombre del enlace (.url):` o `Nombre del archivo (.txt):`.
     - El botón principal cambia a: `Guardar enlace .url en Dropbox` o `Guardar archivo .txt en Dropbox`.
4. Edita el nombre y pulsa el botón guardar: el archivo se almacena en tu Dropbox local y se indexa al instante en el buscador.

---

### 2️⃣ y 3️⃣ Ventana Fijada (2 Columnas, Scroll y Escala Adaptativa)

#### ¿Qué problema se solucionó?
- En monitores estándar (1080p), la ventana fijada abría con una escala agrandada (1.38x) que causaba saltos de línea molestos (ej. *"Última actualización: 09/02 Detenido de momento"* en 2 líneas) y empujaba los botones inferiores fuera de la pantalla.
- La columna izquierda no tenía barra de desplazamiento, por lo que los botones *"Mover a mañana"*, filtros y accesos quedaban inaccesibles.

#### ¿Cómo verificarlo?
1. En la vista de Calendario, haz clic sobre cualquier tarjeta de actividad para **fijarla**.
2. **Verifica la columna izquierda ("ACTIVIDAD SELECCIONADA")**:
   - Todo el texto cabe perfectamente en una sola línea sin cortes tipográficos.
   - El botón *"💾 Guardar cambios"*, la fecha, hora y duración están alineados.
   - Los botones *"T · Terminar…"*, *"Enviar mensaje…"* y *"Mover a mañana"* son visibles de inmediato.
   - Si la actividad tiene muchos comentarios o la ventana se hace pequeña, ahora existe un **scroll vertical independiente** en la columna izquierda que permite acceder a todos los botones.
3. **Verifica la columna derecha ("ACTIVIDADES DEL PROYECTO")**:
   - Se muestran las fases agrupadas (ej. `SUSPENDIDAS`, `REVISIONES`, etc.) con su propio scroll y contadores sincronizados.

---

### 4️⃣ Catálogo de Plantillas (Fase 1 y Redes Sociales)

#### ¿Qué hace?
Organiza las plantillas frecuentes en dos filas con botones compactos para crear actividades rápidamente con las nomenclaturas de cobros, pagos, revisiones y redes.

#### ¿Cómo verificarlo?
1. En la barra superior, haz clic en el botón **⚡ Plantillas**.
2. Verifica la organización en **2 filas**:
   - **Fila 1**: Accesos principales de Cobros (`prtuzCOBRAR`, `prtuzPAGAR`), Revisiones (`prtuzREVISION`) y Biblioteca.
   - **Fila 2**: Redes Sociales divididas en opciones estilizadas y claras.
3. Al hacer clic en cualquier plantilla, se precargan los tags y formatos correctos en el diálogo de creación.

---

### 5️⃣ Vista de Avance Diario y Exclusión de FTF

#### ¿Qué hace?
- Presenta el resumen del día en tarjetas limpias, reduciendo la saturación de colores y texto para una lectura rápida.
- Excluye las actividades clasificadas como **FTF (Face to Face)** del conteo total para no distorsionar las métricas de rendimiento y revisión diaria.

#### ¿Cómo verificarlo?
1. Abre la vista de **Avance Diario** desde el menú lateral o el calendario.
2. Observa el conteo de actividades y las tarjetas de auditoría:
   - Las actividades FTF no se suman como revisiones ni pendientes del flujo habitual.
   - La tipografía y el espaciado son legibles y descansados a la vista.

---

### 6️⃣ Sincronización Notion Icon-Only

#### ¿Qué hace?
El botón de sincronización de Notion en el encabezado ahora se muestra como un icono elegante y compacto sin texto largo, eliminando cualquier desbordamiento o scroll horizontal en pantallas ajustadas.

#### ¿Cómo verificarlo?
1. Revisa la barra de herramientas superior: el botón de sync se mantiene siempre alineado sin desplazar los demás controles del calendario.

---

> [!TIP]
> **Estado de Compilación**: Proyecto verificado con `dotnet build Anfeta.UI.csproj -c Debug` $\to$ **100% exitoso, 0 Errores**.
