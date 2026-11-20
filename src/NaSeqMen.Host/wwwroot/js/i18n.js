const I18N = {
  en: {
    "status.connected": "Connected",
    "status.offline": "Offline",
    "status.paused": "Paused",
    "action.deploy": "Deploy",
    "action.deployed": "Deployed",
    "action.resetDemo": "Reset Demo",
    "action.flash": "Flash",
    "palette.filter": "Filter nodes...",
    "canvas.hint": "Drag nodes from palette · Drag socket to connect · Wheel to zoom · Drag background to pan · Del to remove",
    "tab.debug": "Debug",
    "tab.inspector": "Inspector",
    "tab.info": "Info",
    "debug.filter": "Filter debug...",
    "debug.empty": "No messages yet. Press any key or click the blue trigger button on Keyboard Input.",
    "inspector.empty": "Select a node on the canvas to inspect its ports and parameters.",
    "inspector.general": "General",
    "inspector.title": "Title",
    "inspector.enabled": "Enabled",
    "inspector.parameters": "Parameters",
    "inspector.ports": "Ports",
    "inspector.trigger": "Trigger Now",
    "inspector.delete": "Delete Node",
    "ctx.addNode": "Add Node",
    "ctx.fitView": "Fit View",
    "ctx.trigger": "Trigger Node",
    "ctx.duplicate": "Duplicate",
    "ctx.toggleEnable": "Toggle Enabled",
    "ctx.delete": "Delete",
    "info.summary": "Workspace Overview",
    "info.catalog": "Registered Node Types",
    "info.paradigm": "Data Basket Paradigm",
    "info.paradigmText": "Every event creates a FlowMessage basket (payload, topic, properties, metadata) that travels along wires from output sockets to input sockets.",
  },
  ru: {
    "status.connected": "Подключено",
    "status.offline": "Нет связи",
    "status.paused": "На паузе",
    "action.deploy": "Применить",
    "action.deployed": "Активно",
    "action.resetDemo": "Сброс демо",
    "action.flash": "Вспышки",
    "palette.filter": "Поиск узлов...",
    "canvas.hint": "Перетащите узел из палитры · Тяните порт для связи · Колесо — зум · Фон — панорама · Del — удалить",
    "tab.debug": "Отладка",
    "tab.inspector": "Свойства",
    "tab.info": "Инфо",
    "debug.filter": "Фильтр лога...",
    "debug.empty": "Сообщений пока нет. Нажмите клавишу на клавиатуре или синюю кнопку запуска на узле Keyboard Input.",
    "inspector.empty": "Выберите узел на схеме, чтобы настроить его параметры и увидеть порты.",
    "inspector.general": "Основное",
    "inspector.title": "Название",
    "inspector.enabled": "Включён",
    "inspector.parameters": "Параметры",
    "inspector.ports": "Порты",
    "inspector.trigger": "Запустить сейчас",
    "inspector.delete": "Удалить узел",
    "ctx.addNode": "Добавить узел",
    "ctx.fitView": "Вместить схему",
    "ctx.trigger": "Запустить узел",
    "ctx.duplicate": "Дублировать",
    "ctx.toggleEnable": "Вкл / Выкл",
    "ctx.delete": "Удалить",
    "info.summary": "Обзор рабочей области",
    "info.catalog": "Зарегистрированные типы узлов",
    "info.paradigm": "Парадигма корзинки данных",
    "info.paradigmText": "Каждое событие создаёт сообщение FlowMessage (payload, topic, properties, meta), которое путешествует по линиям от выходных портов к входным.",
  },
};

let currentLang = localStorage.getItem("naseqmen.lang") || "en";

function t(key) {
  return (I18N[currentLang] && I18N[currentLang][key]) || I18N.en[key] || key;
}

function applyI18n() {
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    const key = el.getAttribute("data-i18n");
    el.textContent = t(key);
  });
  document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => {
    const key = el.getAttribute("data-i18n-placeholder");
    el.setAttribute("placeholder", t(key));
  });
  const langBtn = document.getElementById("btn-lang");
  if (langBtn) langBtn.textContent = currentLang.toUpperCase();
}

function toggleLang() {
  currentLang = currentLang === "en" ? "ru" : "en";
  localStorage.setItem("naseqmen.lang", currentLang);
  applyI18n();
}
