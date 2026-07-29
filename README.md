# 🛡️ Fydelis Disk Forensics & Recovery

> Ferramenta avançada de forense digital, recuperação de dados de baixo nível e gerenciamento de partições para ambientes Windows, desenvolvida em C#/.NET com interface WPF.

---

## 🚀 Sobre o Projeto

O **Fydelis Disk Forensics** é um utilitário projetado para engenheiros de sistemas, profissionais de segurança da informação e entusiastas de tecnologia que precisam interagir com dispositivos de armazenamento diretamente a nível de setores e trilhas físicas (`\\.\PhysicalDriveX`). 

O software conta com uma interface em tema escuro estilo terminal hacker/cyber-ops e implementa operações diretas na API do Windows (`CreateFile`, `DeviceIoControl`, `ReadFile`, `WriteSectors`) e WMI.

---

## 🛠️ Funcionalidades Principais

1. **[ 01. Format & Diskpart ]**
   - Formatação segura de volumes (suporte a NTFS, FAT32, exFAT) utilizando WMI e comandos nativos do sistema.
   - Limpeza destrutiva de discos físicos (`Diskpart Clean`) para reestruturação total da tabela de partições.

2. **[ 02. Partition Scan ]**
   - Varredura bruta (Raw Scan) de setores de boot perdidos para recuperação de partições deletadas ou corrompidas.
   - Ferramenta de backup e restauração de MBR (Master Boot Record).

3. **[ 03. File Carving & MFT Recovery ]**
   - Recuperação de arquivos deletados por assinatura de cabeçalho (*File Carving* para JPG, PNG, PDF, ZIP, DOC, MP3, RAR, BMP, EXE, etc.).
   - Leitura de registros MFT deletados em sistemas de arquivos NTFS e exportação seletiva/em lote.

4. **[ 04. Disk Clone (Sector-to-Sector) ]**
   - Clonagem física bit a bit (*bit-stream image*) otimizada com blocos de alta performance (1 MB) para migração ou cópia forense exata entre discos.

---

## 💻 Requisitos do Sistema

- **Sistema Operacional:** Windows 10 / 11 (x64).
- **Runtime:** .NET 6.0 ou superior (WPF).
- **Permissões:** Necessário executar a aplicação **como Administrador** para permitir o acesso de leitura/escrita bruta aos dispositivos físicos do kernel.

---

## ⚙️ Como Compilar e Executar

1. Certifique-se de ter o **Visual Studio 2022** (ou SDK do .NET 6/7/8) instalado com o pacote de desenvolvimento para desktop .NET (*WPF*).
2. Clone ou descompacte o repositório do projeto na sua máquina:
   ```bash
   git clone [https://github.com/teu-usuario/fydelis-disk-recovery.git](https://github.com/teu-usuario/fydelis-disk-recovery.git)
