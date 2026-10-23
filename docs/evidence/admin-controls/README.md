# Contrôles du 23 octobre 2026

Version isolée 1.1.2, préparée depuis le dépôt de développement indiqué par AGENTS.md. Plan coté : [admin-controls.svg](../../wireframes/admin-controls.svg), fenêtre 1320 × 860. Les captures de ce dossier viennent du vrai formulaire WPF du binaire publié, en mode démonstration avec données synthétiques.

## Résultats

- Crash reproduit dans `Motion.Enter` : `TranslateTransform` issu du style figé, impossible d’animer Y. La correction clone la transformation figée avant animation. Les animations du switch ciblent aussi l’ellipse par son chemin de propriété WPF.
- Tests du binaire Windows publié : sortie 0. Bascules répétées des deux switches, confirmations/annulation de fermeture, changement de connexion, profils persistants sur fichiers synthétiques, ouverture du sélecteur et régressions existantes.
- Outil Python Linux : 32 tests, sortie 0 sous WSL.
- Serveur : `npm test`, 245 cas, 243 réussis et 2 tests Unix ignorés sous Windows ; aucun échec. Les trois tests de fermeture ont aussi été exécutés sous Linux, sans cas ignoré, sur un répertoire temporaire et un serveur synthétique isolé du service réel.
- Intégration Linux : l’outil Python a réellement fermé un salon de test par le socket Unix ; déconnexion et suppression contrôlées. Permissions 0600 et refus des commandes mal formées contrôlés.
- Contrôle réel en lecture seule : accès SSH du profil existant valide, service actif et protocole du jeu répondant ; aucun joueur au moment du contrôle. Le profil a été ajouté à la liste locale `connections.json`. `connection.json` et `known_hosts` ont conservé leurs empreintes.

## Périmètre et limites

Le canal serveur de cette branche accepte seulement la fermeture d’un salon. Il ne change ni le protocole des joueurs, ni les comptes SQLite, ni la ROM, les sauvegardes ou les manettes. La fermeture déconnecte les joueurs de la coop ; elle ne sauvegarde pas leur partie.

Le service VPS en production n’a pas été remplacé, afin de préserver le chantier parallèle confirmé par l’utilisateur. Sur le serveur actuel sans ce canal, le bouton signale une indisponibilité. La branche serveur `codex/admin-close-isolated` et l’outil Python de cette branche doivent être intégrés ensemble avant activation réelle. Ne pas mélanger ce canal isolé avec une autre version du socket d’administration.

Revenir au précédent binaire ne change pas la base du jeu. Les anciens profils restent lisibles ; seule la nouvelle liste de connexions cesse d’être affichée par l’ancien panel.
